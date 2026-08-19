// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using Crank.PerfLabExporter.Contracts;
using Crank.PerfLabExporter.Contracts.Policy;
using Crank.PerfLabExporter.Conversion;
using Crank.PerfLabExporter.IO;
using Crank.PerfLabExporter.Naming;
using Crank.PerfLabExporter.Publishing;

namespace Crank.PerfLabExporter.Backfill
{
    internal interface IBackfillClock
    {
        DateTimeOffset UtcNow { get; }
    }

    internal sealed class SystemBackfillClock : IBackfillClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    internal sealed class TrendBackfillRunner
    {
        private readonly ILegacyTrendRepository _repository;
        private readonly CrankPerfLabConverter _converter;
        private readonly CounterPolicy _policy;
        private readonly LegacyTrendMapping _mapping;
        private readonly IPerfLabPublisher? _publisher;
        private readonly BackfillCheckpointStore _checkpointStore;
        private readonly IBackfillClock _clock;
        private readonly ISecretRedactor _redactor;
        private readonly Action<string>? _log;

        public TrendBackfillRunner(
            ILegacyTrendRepository repository,
            CrankPerfLabConverter converter,
            CounterPolicy policy,
            LegacyTrendMapping mapping,
            IPerfLabPublisher? publisher,
            BackfillCheckpointStore checkpointStore,
            IBackfillClock clock,
            ISecretRedactor redactor,
            Action<string>? log = null)
        {
            _repository = repository;
            _converter = converter;
            _policy = policy;
            _mapping = mapping;
            _publisher = publisher;
            _checkpointStore = checkpointStore;
            _clock = clock;
            _redactor = redactor;
            _log = log;
        }

        public async Task<BackfillSummary> RunAsync(
            TrendBackfillExecutionOptions options,
            CancellationToken cancellationToken)
        {
            if (options.BatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "The backfill batch size must be positive.");
            }

            if (options.MaximumRows is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "The maximum row count must be positive when supplied.");
            }

            if (!options.DryRun && _publisher is null)
            {
                throw new InvalidOperationException(
                    "A PerfLab publisher is required for a live backfill.");
            }

            LegacyTrendMappingLoader.Validate(_mapping);
            var existingCheckpoint = await _checkpointStore.LoadAsync(cancellationToken);
            var endUtc = (
                options.EndUtc ??
                existingCheckpoint?.EndUtc ??
                _clock.UtcNow).ToUniversalTime();
            var startUtc = (
                options.StartUtc ??
                existingCheckpoint?.StartUtc ??
                endUtc.AddDays(-90)).ToUniversalTime();
            if (startUtc > endUtc)
            {
                throw new ArgumentException(
                    "The inclusive backfill start UTC must not be later than the end UTC.");
            }

            var configurationFingerprint = BackfillConfigurationFingerprint.Create(
                options,
                startUtc,
                endUtc);
            ValidateCheckpoint(
                existingCheckpoint,
                options.MappingFingerprint,
                configurationFingerprint,
                startUtc,
                endUtc);
            if (existingCheckpoint is null)
            {
                existingCheckpoint = new BackfillCheckpoint
                {
                    ConfigurationFingerprint = configurationFingerprint,
                    MappingFingerprint = options.MappingFingerprint,
                    StartUtc = startUtc,
                    EndUtc = endUtc
                };
                await _checkpointStore.SaveAsync(
                    existingCheckpoint,
                    cancellationToken);
            }

            var summary = new BackfillSummary
            {
                StartUtc = startUtc,
                EndUtc = endUtc,
                Table = options.Table,
                DryRun = options.DryRun,
                MappingFingerprint = options.MappingFingerprint,
                ConfigurationFingerprint = configurationFingerprint
            };
            var readCursor =
                existingCheckpoint.LastCompletedSqlDateTimeUtc is { } timestamp &&
                existingCheckpoint.LastCompletedSqlId is { } id
                    ? new LegacyTrendCursor(timestamp, id)
                    : null;
            var checkpointBlocked = false;
            var reachedEnd = false;
            while (!reachedEnd &&
                (options.MaximumRows is null || summary.Scanned < options.MaximumRows))
            {
                var remaining = options.MaximumRows is null
                    ? options.BatchSize
                    : Math.Min(options.BatchSize, options.MaximumRows.Value - summary.Scanned);
                IReadOnlyList<LegacyTrendRow> rows;
                try
                {
                    rows = await _repository.ReadBatchAsync(
                        new LegacyTrendQuery(startUtc, endUtc, readCursor, remaining),
                        cancellationToken);
                    ValidateBatch(rows, startUtc, endUtc, readCursor, remaining);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    summary.Failed++;
                    summary.Issues.Add(new BackfillIssue(
                        null,
                        "failed",
                        _redactor.Redact(exception.Message)));
                    break;
                }

                if (rows.Count == 0)
                {
                    break;
                }

                reachedEnd = rows.Count < remaining;
                foreach (var row in rows)
                {
                    summary.Scanned++;
                    readCursor = new LegacyTrendCursor(row.DateTimeUtc, row.Id);
                    if (row.Excluded)
                    {
                        summary.Excluded++;
                        if (!checkpointBlocked)
                        {
                            checkpointBlocked = !await TryAdvanceCheckpointAsync(
                                row,
                                options,
                                configurationFingerprint,
                                startUtc,
                                endUtc,
                                summary,
                                cancellationToken);
                        }

                        continue;
                    }

                    try
                    {
                        var execution = LegacyJobResultsAdapter.Adapt(row);
                        var mappingMatch = LegacyTrendMappingMatcher.Resolve(
                            _mapping,
                            row,
                            execution);
                        var identity = LegacyExportIdentityBuilder.Build(
                            row,
                            execution,
                            _mapping,
                            mappingMatch,
                            options.MappingFingerprint,
                            options.Table,
                            options.Identity);
                        var conversion = await _converter.ConvertAsync(
                            execution,
                            _policy,
                            identity,
                            new ExportSourceMetadata(
                                $"sql:{options.Table}:{row.Id.ToString(CultureInfo.InvariantCulture)}",
                                options.PolicySourceName,
                                $"legacy-mapping:{options.MappingSourceName}:{options.MappingFingerprint}"),
                            new CrankConversionOptions(
                                ValidateScenarioProperty: false),
                            cancellationToken);
                        var names = ExportNaming.Create(conversion.Report, identity);
                        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(
                            conversion.Report,
                            ContractJson.CreateSerializerOptions(writeIndented: true));
                        var outputPath = Path.Combine(
                            Path.GetFullPath(options.OutputDirectory),
                            names.FileName);
                        await AtomicFileWriter.WriteAsync(
                            outputPath,
                            reportBytes,
                            cancellationToken);
                        summary.Converted++;

                        foreach (var diagnostic in conversion.Diagnostics)
                        {
                            _log?.Invoke(
                                $"row {row.Id.ToString(CultureInfo.InvariantCulture)}: {diagnostic}");
                        }

                        if (options.DryRun)
                        {
                            summary.DryRunValidated++;
                        }
                        else
                        {
                            await _publisher!.PublishAsync(
                                options.Container!,
                                options.Queue!,
                                names.BlobName,
                                reportBytes,
                                cancellationToken);
                            summary.Uploaded++;
                        }

                        if (!checkpointBlocked)
                        {
                            checkpointBlocked = !await TryAdvanceCheckpointAsync(
                                row,
                                options,
                                configurationFingerprint,
                                startUtc,
                                endUtc,
                                summary,
                                cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (
                        exception is LegacyMappingResolutionException or
                        LegacyIdentityResolutionException)
                    {
                        checkpointBlocked = true;
                        summary.Unresolved++;
                        summary.Issues.Add(new BackfillIssue(
                            row.Id,
                            "unresolved",
                            _redactor.Redact(exception.Message)));
                    }
                    catch (Exception exception)
                    {
                        checkpointBlocked = true;
                        summary.Failed++;
                        summary.Issues.Add(new BackfillIssue(
                            row.Id,
                            "failed",
                            _redactor.Redact(exception.Message)));
                    }
                }
            }

            return summary;
        }

        private async Task<bool> TryAdvanceCheckpointAsync(
            LegacyTrendRow row,
            TrendBackfillExecutionOptions options,
            string configurationFingerprint,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            BackfillSummary summary,
            CancellationToken cancellationToken)
        {
            try
            {
                await _checkpointStore.SaveAsync(
                    new BackfillCheckpoint
                    {
                        ConfigurationFingerprint = configurationFingerprint,
                        MappingFingerprint = options.MappingFingerprint,
                        StartUtc = startUtc,
                        EndUtc = endUtc,
                        LastCompletedSqlId = row.Id,
                        LastCompletedSqlDateTimeUtc = row.DateTimeUtc.ToUniversalTime()
                    },
                    cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                summary.Failed++;
                summary.Issues.Add(new BackfillIssue(
                    row.Id,
                    "failed",
                    _redactor.Redact(
                        $"Checkpoint update failed: {exception.Message}")));
                return false;
            }
        }

        private static void ValidateCheckpoint(
            BackfillCheckpoint? checkpoint,
            string mappingFingerprint,
            string configurationFingerprint,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc)
        {
            if (checkpoint is null)
            {
                return;
            }

            if (checkpoint.SchemaVersion != 1)
            {
                throw new InvalidDataException(
                    $"Checkpoint schema version {checkpoint.SchemaVersion} is incompatible; expected 1.");
            }

            if (!string.Equals(
                    checkpoint.MappingFingerprint,
                    mappingFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Checkpoint mapping fingerprint is incompatible with the configured legacy mapping.");
            }

            if (!string.Equals(
                    checkpoint.ConfigurationFingerprint,
                    configurationFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Checkpoint configuration fingerprint is incompatible with this backfill invocation.");
            }

            if (checkpoint.StartUtc.ToUniversalTime() != startUtc ||
                checkpoint.EndUtc.ToUniversalTime() != endUtc)
            {
                throw new InvalidDataException(
                    "Checkpoint UTC bounds are incompatible with this backfill invocation.");
            }

            if (checkpoint.LastCompletedSqlDateTimeUtc.HasValue !=
                checkpoint.LastCompletedSqlId.HasValue)
            {
                throw new InvalidDataException(
                    "Checkpoint last-completed timestamp and ID must both be present or absent.");
            }

            if (checkpoint.LastCompletedSqlDateTimeUtc is { } cursorTimestamp &&
                (cursorTimestamp < startUtc ||
                 cursorTimestamp > endUtc))
            {
                throw new InvalidDataException(
                    "Checkpoint cursor is outside the configured UTC bounds.");
            }
        }

        private static void ValidateBatch(
            IReadOnlyList<LegacyTrendRow> rows,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            LegacyTrendCursor? after,
            int requestedCount)
        {
            if (rows.Count > requestedCount)
            {
                throw new InvalidDataException(
                    "The legacy SQL repository returned more rows than requested.");
            }

            var cursor = after;
            foreach (var row in rows)
            {
                var timestamp = row.DateTimeUtc.ToUniversalTime();
                if (timestamp < startUtc || timestamp > endUtc)
                {
                    throw new InvalidDataException(
                        $"Legacy SQL row {row.Id} is outside the inclusive UTC bounds.");
                }

                if (cursor is not null &&
                    (timestamp < cursor.DateTimeUtc ||
                     (timestamp == cursor.DateTimeUtc && row.Id <= cursor.Id)))
                {
                    throw new InvalidDataException(
                        "The legacy SQL repository did not return strict DateTimeUtc/Id ordering.");
                }

                cursor = new LegacyTrendCursor(timestamp, row.Id);
            }
        }
    }
}
