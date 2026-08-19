// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Crank.PerfLabExporter.Backfill;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Contracts;
using Crank.PerfLabExporter.Conversion;
using Crank.PerfLabExporter.Publishing;

namespace Crank.PerfLabExporter.Tests
{
    internal static class BackfillTestData
    {
        public static LegacyTrendRow CreateRow(
            long id,
            DateTimeOffset timestamp,
            bool excluded = false,
            string scenario = "Plaintext",
            string profile = "gold-lin-app",
            string? description = null,
            string? documentScenario = null,
            string? document = null)
        {
            if (document is null)
            {
                var execution = FixtureLoader.LoadExecution();
                foreach (var property in execution.JobResults.Properties.Keys
                    .Where(key => key.StartsWith(
                        "perflab.",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    execution.JobResults.Properties.Remove(property);
                }

                execution.JobResults.Properties["scenario"] =
                    documentScenario ?? scenario;
                execution.JobResults.Properties["buildId"] = $"build-{id}";
                execution.JobResults.Properties["buildNumber"] = $"20260819.{id}";
                execution.JobResults.Jobs["application"].Variables["profile"] =
                    JsonSerializer.SerializeToElement(profile);
                execution.JobResults.Jobs["application"].Environment["machineName"] =
                    JsonSerializer.SerializeToElement("sut-machine");
                execution.JobResults.Jobs["load"].Environment["machineName"] =
                    JsonSerializer.SerializeToElement("load-machine");
                document = JsonSerializer.Serialize(
                    execution.JobResults,
                    ContractJson.CreateSerializerOptions());
            }

            return new LegacyTrendRow(
                id,
                excluded,
                timestamp,
                $"session-{id}",
                scenario,
                description ?? $"{scenario} 8- Trends gold-lin",
                document);
        }

        public static BackfillIdentityOptions CreateIdentityOptions(
            string benchmarksCommit =
                "2222222222222222222222222222222222222222")
        {
            return new BackfillIdentityOptions
            {
                RuntimeBranch = "main",
                BenchmarksCommit = benchmarksCommit,
                CrankVersion = "0.2.0-alpha.backfill",
                AzureDevOpsProject = "internal",
                AzureDevOpsPipeline = "aspnet-benchmarks",
                AzureDevOpsBuildUrlTemplate =
                    "https://dev.azure.com/example/internal/_build/results?buildId={buildId}"
            };
        }

        public static TrendBackfillExecutionOptions CreateOptions(
            TemporaryDirectory directory,
            LoadedLegacyTrendMapping mapping,
            DateTimeOffset? startUtc,
            DateTimeOffset? endUtc,
            bool dryRun = true,
            int batchSize = 100,
            int? maximumRows = null,
            BackfillIdentityOptions? identity = null,
            string? mappingFingerprint = null)
        {
            return new TrendBackfillExecutionOptions(
                startUtc,
                endUtc,
                batchSize,
                maximumRows,
                dryRun,
                "TrendBenchmarks",
                "server|database",
                SqlAuthenticationMode.ConnectionString.ToString(),
                mappingFingerprint ?? mapping.Fingerprint,
                mapping.SourceName,
                "policy-fingerprint",
                "counter-policy.json",
                directory.Path,
                System.IO.Path.Combine(
                    directory.Path,
                    "checkpoint.json"),
                dryRun ? null : "account",
                dryRun ? null : "results",
                dryRun ? null : "resultsqueue",
                identity ?? CreateIdentityOptions());
        }

        public static LoadedLegacyTrendMapping LoadMapping()
        {
            return LegacyTrendMappingLoader.LoadAsync(
                FixtureLoader.GetPath("trend-perflab-legacy-mapping.json"),
                CancellationToken.None).GetAwaiter().GetResult();
        }

        public static TrendBackfillRunner CreateRunner(
            FakeLegacyTrendRepository repository,
            LoadedLegacyTrendMapping mapping,
            TemporaryDirectory directory,
            FixedCommitTimeResolver resolver,
            IPerfLabPublisher? publisher = null,
            IBackfillClock? clock = null,
            ISecretRedactor? redactor = null)
        {
            return new TrendBackfillRunner(
                repository,
                new CrankPerfLabConverter(resolver),
                FixtureLoader.LoadPolicy(),
                mapping.Mapping,
                publisher,
                new BackfillCheckpointStore(
                    System.IO.Path.Combine(
                        directory.Path,
                        "checkpoint.json")),
                clock ?? new FixedBackfillClock(
                    DateTimeOffset.Parse("2026-08-19T12:00:00Z")),
                redactor ?? new NullSecretRedactor());
        }
    }

    internal sealed class FakeLegacyTrendRepository : ILegacyTrendRepository
    {
        public FakeLegacyTrendRepository(IEnumerable<LegacyTrendRow> rows)
        {
            Rows = rows.ToList();
        }

        public List<LegacyTrendRow> Rows { get; }

        public List<LegacyTrendQuery> Queries { get; } = [];

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<LegacyTrendRow>> ReadBatchAsync(
            LegacyTrendQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            if (Failure is not null)
            {
                throw Failure;
            }

            var result = Rows
                .Where(row =>
                    row.DateTimeUtc >= query.StartUtc &&
                    row.DateTimeUtc <= query.EndUtc)
                .Where(row =>
                    query.After is null ||
                    row.DateTimeUtc > query.After.DateTimeUtc ||
                    (row.DateTimeUtc == query.After.DateTimeUtc &&
                     row.Id > query.After.Id))
                .OrderBy(row => row.DateTimeUtc)
                .ThenBy(row => row.Id)
                .Take(query.BatchSize)
                .ToList();
            return Task.FromResult<IReadOnlyList<LegacyTrendRow>>(result);
        }
    }

    internal sealed class FakePerfLabPublisher : IPerfLabPublisher
    {
        public int FailuresRemaining { get; set; }

        public int Attempts { get; private set; }

        public List<string> BlobNames { get; } = [];

        public Task<string> PublishAsync(
            string container,
            string queue,
            string blobName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            BlobNames.Add(blobName);
            if (FailuresRemaining-- > 0)
            {
                throw new InvalidOperationException("publisher failure");
            }

            return Task.FromResult(
                PerfLabPublisher.CreateQueueMessage(container, blobName));
        }
    }

    internal sealed class FixedCommitTimeResolver : ICommitTimeResolver
    {
        public FixedCommitTimeResolver(DateTimeOffset timestamp)
        {
            Timestamp = timestamp;
        }

        public DateTimeOffset Timestamp { get; }

        public int CallCount { get; private set; }

        public string? Repository { get; private set; }

        public string? CommitHash { get; private set; }

        public Task<DateTimeOffset> ResolveAsync(
            string repository,
            string commitHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Repository = repository;
            CommitHash = commitHash;
            return Task.FromResult(Timestamp);
        }
    }

    internal sealed class FixedBackfillClock : IBackfillClock
    {
        public FixedBackfillClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "backfill-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
