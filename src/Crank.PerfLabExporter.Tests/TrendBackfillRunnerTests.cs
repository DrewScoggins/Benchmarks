// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Crank.PerfLabExporter.Backfill;
using Crank.PerfLabExporter.Contracts;
using Crank.PerfLabExporter.Contracts.PerfLab;

namespace Crank.PerfLabExporter.Tests
{
    public class TrendBackfillRunnerTests
    {
        [Fact]
        public async Task UsesInclusiveLatestNinetyDayBoundsAndPagesInStableOrder()
        {
            using var directory = new TemporaryDirectory();
            var now = DateTimeOffset.Parse("2026-08-19T12:00:00Z");
            var start = now.AddDays(-90);
            var rows = new[]
            {
                BackfillTestData.CreateRow(1, start.AddSeconds(-1)),
                BackfillTestData.CreateRow(2, start),
                BackfillTestData.CreateRow(
                    3,
                    start,
                    excluded: true,
                    document: "not-json"),
                BackfillTestData.CreateRow(4, start.AddDays(1)),
                BackfillTestData.CreateRow(5, now),
                BackfillTestData.CreateRow(6, now.AddSeconds(1))
            };
            var repository = new FakeLegacyTrendRepository(rows);
            var mapping = BackfillTestData.LoadMapping();
            var runner = BackfillTestData.CreateRunner(
                repository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")),
                clock: new FixedBackfillClock(now));
            var options = BackfillTestData.CreateOptions(
                directory,
                mapping,
                startUtc: null,
                endUtc: null,
                dryRun: true,
                batchSize: 2);

            var summary = await runner.RunAsync(
                options,
                CancellationToken.None);

            Assert.Equal(start, summary.StartUtc);
            Assert.Equal(now, summary.EndUtc);
            Assert.Equal(4, summary.Scanned);
            Assert.Equal(1, summary.Excluded);
            Assert.Equal(3, summary.Converted);
            Assert.Equal(3, summary.DryRunValidated);
            Assert.Equal(0, summary.Uploaded);
            Assert.True(repository.Queries.Count >= 2);
            Assert.All(repository.Queries, query =>
            {
                Assert.Equal(start, query.StartUtc);
                Assert.Equal(now, query.EndUtc);
                Assert.InRange(query.BatchSize, 1, 2);
            });
            Assert.Equal(
                new long[] { 2, 3, 4, 5 },
                repository.Queries
                    .SelectMany(query => rows
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
                        .Select(row => row.Id))
                    .Distinct()
                    .ToArray());
            Assert.True(File.Exists(options.CheckpointPath));
        }

        [Fact]
        public async Task UsesRuntimeCommitTimestampAndPreservesHistoricalTraceability()
        {
            using var directory = new TemporaryDirectory();
            var sqlTimestamp =
                DateTimeOffset.Parse("2026-08-19T10:00:00Z");
            var commitTimestamp =
                DateTimeOffset.Parse("2026-08-17T07:45:00Z");
            var repository = new FakeLegacyTrendRepository(
                [BackfillTestData.CreateRow(
                    100,
                    sqlTimestamp,
                    scenario: "plaintext",
                    description: "Plaintext 8- Trends gold-lin",
                    documentScenario: "plaintext")]);
            var mapping = BackfillTestData.LoadMapping();
            var resolver = new FixedCommitTimeResolver(commitTimestamp);
            var runner = BackfillTestData.CreateRunner(
                repository,
                mapping,
                directory,
                resolver);
            var options = BackfillTestData.CreateOptions(
                directory,
                mapping,
                sqlTimestamp.AddMinutes(-1),
                sqlTimestamp.AddMinutes(1));

            var summary = await runner.RunAsync(
                options,
                CancellationToken.None);

            Assert.Equal(1, summary.Converted);
            var resultPath = Assert.Single(
                Directory.GetFiles(
                    directory.Path,
                    "*.perflab.json"));
            var report = JsonSerializer.Deserialize<PerfLabReport>(
                File.ReadAllText(resultPath),
                ContractJson.CreateSerializerOptions());
            Assert.NotNull(report);
            Assert.Equal(commitTimestamp, report.Build.TimeStamp);
            Assert.NotEqual(sqlTimestamp, report.Build.TimeStamp);
            Assert.Equal("aspnet-plaintext", report.Run.Name);
            Assert.Equal(
                "Ubuntu.2204.Amd64.AspNetGold.Perf",
                report.Run.Queue);
            Assert.Equal("sut-machine", report.Os.MachineName);
            Assert.Equal("56", report.Run.Configurations["Cores"]);
            Assert.Equal("SUT+Load", report.Run.Configurations["Topology"]);
            Assert.Equal("Plaintext", Assert.Single(report.Tests).Name);
            Assert.Equal(
                "plaintext",
                Assert.Single(report.Tests).AdditionalData[
                    "crank.property.scenario"]);
            Assert.Equal(
                sqlTimestamp.ToString("O"),
                report.Build.AdditionalData[
                    "historical.known.sql.insertionTimeUtc"]);
            Assert.Equal(
                "0.2.0-alpha.backfill",
                report.Build.AdditionalData[
                    "historical.fallback.crankVersion"]);
            Assert.Equal(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                report.Build.AdditionalData["aspnetCoreGitHash"]);
            Assert.Equal(1, resolver.CallCount);
            Assert.Equal(
                "1111111111111111111111111111111111111111",
                resolver.CommitHash);
        }

        [Fact]
        public async Task ResumesAfterLastCompletedTimestampAndId()
        {
            using var directory = new TemporaryDirectory();
            var timestamp =
                DateTimeOffset.Parse("2026-08-19T10:00:00Z");
            var repository = new FakeLegacyTrendRepository(
            [
                BackfillTestData.CreateRow(10, timestamp),
                BackfillTestData.CreateRow(11, timestamp)
            ]);
            var mapping = BackfillTestData.LoadMapping();
            var publisher = new FakePerfLabPublisher();
            var firstRunner = BackfillTestData.CreateRunner(
                repository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")),
                publisher);
            var firstOptions = BackfillTestData.CreateOptions(
                directory,
                mapping,
                timestamp.AddMinutes(-1),
                timestamp.AddMinutes(1),
                dryRun: false,
                maximumRows: 1);

            var first = await firstRunner.RunAsync(
                firstOptions,
                CancellationToken.None);

            Assert.Equal(1, first.Uploaded);
            var secondRepository = new FakeLegacyTrendRepository(repository.Rows);
            var secondRunner = BackfillTestData.CreateRunner(
                secondRepository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")),
                publisher);
            var secondOptions = BackfillTestData.CreateOptions(
                directory,
                mapping,
                timestamp.AddMinutes(-1),
                timestamp.AddMinutes(1),
                dryRun: false);

            var second = await secondRunner.RunAsync(
                secondOptions,
                CancellationToken.None);

            Assert.Equal(1, second.Scanned);
            Assert.Equal(1, second.Uploaded);
            Assert.Equal(2, publisher.Attempts);
            Assert.Equal(10, Assert.Single(secondRepository.Queries).After!.Id);
        }

        [Fact]
        public async Task RejectsIncompatibleCheckpointFingerprint()
        {
            using var directory = new TemporaryDirectory();
            var timestamp =
                DateTimeOffset.Parse("2026-08-19T10:00:00Z");
            var repository = new FakeLegacyTrendRepository(
                [BackfillTestData.CreateRow(20, timestamp)]);
            var mapping = BackfillTestData.LoadMapping();
            var runner = BackfillTestData.CreateRunner(
                repository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")));
            var options = BackfillTestData.CreateOptions(
                directory,
                mapping,
                timestamp.AddMinutes(-1),
                timestamp.AddMinutes(1));
            _ = await runner.RunAsync(options, CancellationToken.None);

            var incompatible = BackfillTestData.CreateOptions(
                directory,
                mapping,
                timestamp.AddMinutes(-1),
                timestamp.AddMinutes(1),
                mappingFingerprint: "different");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                runner.RunAsync(incompatible, CancellationToken.None));

            Assert.Contains(
                "mapping fingerprint",
                exception.Message,
                StringComparison.Ordinal);

            var changedIdentity = BackfillTestData.CreateOptions(
                directory,
                mapping,
                timestamp.AddMinutes(-1),
                timestamp.AddMinutes(1),
                identity: BackfillTestData.CreateIdentityOptions(
                    benchmarksCommit: "different-benchmarks-commit"));
            var configurationException =
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    runner.RunAsync(
                        changedIdentity,
                        CancellationToken.None));
            Assert.Contains(
                "configuration fingerprint",
                configurationException.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task DoesNotAdvanceCheckpointPastPublicationFailure()
        {
            using var directory = new TemporaryDirectory();
            var timestamp =
                DateTimeOffset.Parse("2026-08-19T10:00:00Z");
            var repository = new FakeLegacyTrendRepository(
            [
                BackfillTestData.CreateRow(30, timestamp),
                BackfillTestData.CreateRow(31, timestamp.AddMinutes(1))
            ]);
            var mapping = BackfillTestData.LoadMapping();
            var publisher = new FakePerfLabPublisher
            {
                FailuresRemaining = 1
            };
            var runner = BackfillTestData.CreateRunner(
                repository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")),
                publisher);
            var options = BackfillTestData.CreateOptions(
                directory,
                mapping,
                startUtc: null,
                endUtc: null,
                dryRun: false);

            var summary = await runner.RunAsync(
                options,
                CancellationToken.None);

            Assert.Equal(2, summary.Converted);
            Assert.Equal(1, summary.Uploaded);
            Assert.Equal(1, summary.Failed);
            var checkpoint = JsonSerializer.Deserialize<BackfillCheckpoint>(
                File.ReadAllText(options.CheckpointPath),
                ContractJson.CreateSerializerOptions());
            Assert.NotNull(checkpoint);
            Assert.Null(checkpoint.LastCompletedSqlId);
            Assert.Null(checkpoint.LastCompletedSqlDateTimeUtc);
            Assert.Equal(2, publisher.Attempts);

            var retryRepository = new FakeLegacyTrendRepository(repository.Rows);
            var retryPublisher = new FakePerfLabPublisher();
            var retryRunner = BackfillTestData.CreateRunner(
                retryRepository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")),
                retryPublisher,
                new FixedBackfillClock(
                    DateTimeOffset.Parse("2026-08-20T12:00:00Z")));
            var retry = await retryRunner.RunAsync(
                options,
                CancellationToken.None);

            Assert.Equal(
                DateTimeOffset.Parse("2026-08-19T12:00:00Z"),
                retry.EndUtc);
            Assert.Equal(2, retry.Uploaded);
            Assert.All(
                retryRepository.Queries,
                query => Assert.Equal(retry.EndUtc, query.EndUtc));
        }

        [Fact]
        public async Task ReportsUnresolvedRowsWithoutAdvancingCheckpoint()
        {
            using var directory = new TemporaryDirectory();
            var timestamp =
                DateTimeOffset.Parse("2026-08-19T10:00:00Z");
            var repository = new FakeLegacyTrendRepository(
                [BackfillTestData.CreateRow(
                    40,
                    timestamp,
                    scenario: "UnknownScenario")]);
            var mapping = BackfillTestData.LoadMapping();
            var runner = BackfillTestData.CreateRunner(
                repository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")));
            var options = BackfillTestData.CreateOptions(
                directory,
                mapping,
                timestamp.AddMinutes(-1),
                timestamp.AddMinutes(1));

            var summary = await runner.RunAsync(
                options,
                CancellationToken.None);

            Assert.Equal(1, summary.Unresolved);
            Assert.Equal(40, Assert.Single(summary.Issues).RowId);
            var checkpoint = JsonSerializer.Deserialize<BackfillCheckpoint>(
                File.ReadAllText(options.CheckpointPath),
                ContractJson.CreateSerializerOptions());
            Assert.NotNull(checkpoint);
            Assert.Null(checkpoint.LastCompletedSqlId);
            Assert.Null(checkpoint.LastCompletedSqlDateTimeUtc);
        }

        [Fact]
        public async Task RedactsConnectionSecretsFromMachineReadableFailures()
        {
            using var directory = new TemporaryDirectory();
            const string connectionString =
                "Server=example;Database=benchmarks;Application Name=sensitive-marker";
            var repository = new FakeLegacyTrendRepository([])
            {
                Failure = new InvalidOperationException(
                    $"Could not open {connectionString}")
            };
            var mapping = BackfillTestData.LoadMapping();
            var runner = BackfillTestData.CreateRunner(
                repository,
                mapping,
                directory,
                new FixedCommitTimeResolver(
                    DateTimeOffset.Parse("2026-08-18T09:30:00Z")),
                redactor: SecretRedactor.Create(connectionString));
            var options = BackfillTestData.CreateOptions(
                directory,
                mapping,
                DateTimeOffset.Parse("2026-08-19T09:00:00Z"),
                DateTimeOffset.Parse("2026-08-19T11:00:00Z"));

            var summary = await runner.RunAsync(
                options,
                CancellationToken.None);
            var json = JsonSerializer.Serialize(
                summary,
                ContractJson.CreateSerializerOptions());

            Assert.Equal(1, summary.Failed);
            Assert.DoesNotContain("sensitive-marker", json, StringComparison.Ordinal);
            Assert.DoesNotContain(connectionString, json, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        }
    }
}
