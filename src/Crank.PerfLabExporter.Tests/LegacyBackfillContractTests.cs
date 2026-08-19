// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.RegularExpressions;
using Crank.PerfLabExporter.Backfill;
using Crank.PerfLabExporter.Contracts;

namespace Crank.PerfLabExporter.Tests
{
    public class LegacyBackfillContractTests
    {
        [Fact]
        public void AdaptsLegacyJobResultsWithoutDroppingPayload()
        {
            var row = BackfillTestData.CreateRow(
                42,
                DateTimeOffset.Parse("2026-08-19T10:00:00Z"));

            var execution = LegacyJobResultsAdapter.Adapt(row);

            Assert.Equal(0, execution.ReturnCode);
            Assert.Equal("Plaintext", execution.JobResults.Properties["scenario"]);
            var application = execution.JobResults.Jobs["application"];
            Assert.NotEmpty(application.Dependencies);
            Assert.NotEmpty(application.Environment);
            Assert.NotEmpty(application.Results);
            Assert.Equal(
                "gold-lin-app",
                application.Variables["profile"].GetString());
        }

        [Fact]
        public void MatchesVersionControlledLaneAndScenarioFamily()
        {
            var loaded = BackfillTestData.LoadMapping();
            var row = BackfillTestData.CreateRow(
                43,
                DateTimeOffset.Parse("2026-08-19T10:00:00Z"),
                scenario: "FortunesMinimalApis",
                profile: "cobalt-hosted-lin-server-azure-linux3-28-app",
                description:
                    "Fortunes Minimal APIs 20- Trends Database cobalt-hosted-lin-azl3-28");
            var execution = LegacyJobResultsAdapter.Adapt(row);

            var match = LegacyTrendMappingMatcher.Resolve(
                loaded.Mapping,
                row,
                execution);

            Assert.Equal("cobalt-hosted-lin-azl3-28", match.Lane.Id);
            Assert.Equal(
                "AzureLinux.3.Amd64.AspNetCobaltHosted.Perf",
                match.Lane.Queue);
            Assert.Equal("aspnet-fortunes", match.Scenario.Family);
            Assert.Equal("SUT+Load+DB", match.Scenario.Topology);
        }

        [Fact]
        public void UsesCanonicalDocumentScenarioWhenSqlScenarioIsLegacyName()
        {
            var loaded = BackfillTestData.LoadMapping();
            var row = BackfillTestData.CreateRow(
                46,
                DateTimeOffset.Parse("2026-08-19T10:00:00Z"),
                scenario: "plaintext",
                documentScenario: "Plaintext");
            var execution = LegacyJobResultsAdapter.Adapt(row);
            var match = LegacyTrendMappingMatcher.Resolve(
                loaded.Mapping,
                row,
                execution);

            var identity = LegacyExportIdentityBuilder.Build(
                row,
                execution,
                loaded.Mapping,
                match,
                loaded.Fingerprint,
                "TrendBenchmarks",
                BackfillTestData.CreateIdentityOptions());

            Assert.Equal("Plaintext", identity.Scenario.Name);
            Assert.Equal("aspnet-plaintext", identity.Scenario.Family);
            Assert.Equal(
                "plaintext",
                identity.AdditionalData["historical.known.sql.scenario"]);
            Assert.Equal(
                "Plaintext",
                identity.AdditionalData["historical.known.canonicalScenario"]);
        }

        [Fact]
        public void ReportsAmbiguousAndUnresolvedRules()
        {
            var loaded = BackfillTestData.LoadMapping();
            var row = BackfillTestData.CreateRow(
                44,
                DateTimeOffset.Parse("2026-08-19T10:00:00Z"));
            var execution = LegacyJobResultsAdapter.Adapt(row);
            loaded.Mapping.LaneRules.Add(new LegacyLaneRule
            {
                Id = "duplicate-gold",
                MatchAny =
                [
                    new LegacyMatchCondition
                    {
                        Source = "profile",
                        EqualTo = "gold-lin-app"
                    }
                ],
                Name = "duplicate",
                Queue = "duplicate",
                Os = "duplicate",
                Architecture = "x64",
                Locale = "en-US",
                Cores = 1,
                Hardware = "duplicate"
            });

            var ambiguous = Assert.Throws<LegacyMappingResolutionException>(() =>
                LegacyTrendMappingMatcher.Resolve(
                    loaded.Mapping,
                    row,
                    execution));
            Assert.Contains("multiple rules matched", ambiguous.Message, StringComparison.Ordinal);
            Assert.Contains("duplicate-gold", ambiguous.Message, StringComparison.Ordinal);

            loaded.Mapping.LaneRules.RemoveAt(
                loaded.Mapping.LaneRules.Count - 1);
            var unknown = BackfillTestData.CreateRow(
                44,
                DateTimeOffset.Parse("2026-08-19T10:00:00Z"),
                scenario: "UnknownScenario",
                description: "Unknown scenario Trends gold-lin",
                documentScenario: "UnknownScenario");
            var unresolved = Assert.Throws<LegacyMappingResolutionException>(() =>
                LegacyTrendMappingMatcher.Resolve(
                    loaded.Mapping,
                    unknown,
                    LegacyJobResultsAdapter.Adapt(unknown)));
            Assert.Contains("no rules matched", unresolved.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUnsafeSqlIdentifiersAndQuotesValidIdentifiers()
        {
            var identifier = SqlTableIdentifier.Parse("dbo.TrendBenchmarks");

            Assert.Equal("dbo.TrendBenchmarks", identifier.CanonicalName);
            Assert.Equal("[dbo].[TrendBenchmarks]", identifier.QuotedName);
            Assert.Throws<ArgumentException>(() =>
                SqlTableIdentifier.Parse(
                    "TrendBenchmarks; DROP TABLE TrendBenchmarks"));
            Assert.Throws<ArgumentException>(() =>
                SqlTableIdentifier.Parse("[dbo].[TrendBenchmarks]"));
        }

        [Fact]
        public void InvalidSqlConnectionStringDoesNotEchoItsValue()
        {
            const string marker = "sensitive-marker";

            var exception = Assert.Throws<ArgumentException>(() =>
                SqlConnectionStringResolver.Resolve(
                    $"not-a-connection-string-{marker}",
                    environmentVariable: null));

            Assert.DoesNotContain(marker, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void LegacyDocumentIsJobResultsRatherThanExecutionWrapper()
        {
            var execution = FixtureLoader.LoadExecution();
            var row = BackfillTestData.CreateRow(
                45,
                DateTimeOffset.Parse("2026-08-19T10:00:00Z"),
                document: JsonSerializer.Serialize(
                    execution.JobResults,
                    ContractJson.CreateSerializerOptions()));

            var adapted = LegacyJobResultsAdapter.Adapt(row);

            Assert.Equal(execution.JobResults.Properties, adapted.JobResults.Properties);
            Assert.Equal(
                execution.JobResults.Jobs.Keys.Order(),
                adapted.JobResults.Jobs.Keys.Order());
        }

        [Fact]
        public void VersionControlledRulesMatchEveryLiveLaneAndScenario()
        {
            var loaded = BackfillTestData.LoadMapping();
            Assert.Equal(
                "net11.0",
                loaded.Mapping.DefaultConfigurations["Framework"]);
            Assert.Equal(
                "CoreCLR",
                loaded.Mapping.DefaultConfigurations["Runtime"]);
            Assert.Equal(
                "Release",
                loaded.Mapping.DefaultConfigurations["Configuration"]);
            using var lanes = JsonDocument.Parse(
                File.ReadAllText(
                    FixtureLoader.GetPath("trend-perflab-lanes.json")));
            foreach (var lane in lanes.RootElement
                .GetProperty("lanes")
                .EnumerateObject())
            {
                var row = BackfillTestData.CreateRow(
                    1000,
                    DateTimeOffset.Parse("2026-08-19T10:00:00Z"),
                    profile: "unknown-app",
                    description: $"Plaintext Trends {lane.Name}");
                var match = LegacyTrendMappingMatcher.Resolve(
                    loaded.Mapping,
                    row,
                    LegacyJobResultsAdapter.Adapt(row));
                var expected = lane.Value;
                Assert.Equal(
                    expected.GetProperty("name").GetString(),
                    match.Lane.Name);
                Assert.Equal(
                    expected.GetProperty("queue").GetString(),
                    match.Lane.Queue);
                Assert.Equal(
                    expected.GetProperty("os").GetString(),
                    match.Lane.Os);
                Assert.Equal(
                    expected.GetProperty("architecture").GetString(),
                    match.Lane.Architecture);
                Assert.Equal(
                    expected.GetProperty("locale").GetString(),
                    match.Lane.Locale);
                Assert.Equal(
                    expected.GetProperty("cores").GetInt32(),
                    match.Lane.Cores);
                Assert.Equal(
                    expected.GetProperty("hardware").GetString(),
                    match.Lane.Hardware);
            }

            AssertScenarioParity(
                loaded,
                "trend-scenarios.yml",
                "SUT+Load");
            AssertScenarioParity(
                loaded,
                "trend-database-scenarios.yml",
                "SUT+Load+DB");
        }

        private static void AssertScenarioParity(
            LoadedLegacyTrendMapping loaded,
            string fixture,
            string expectedTopology)
        {
            var text = File.ReadAllText(FixtureLoader.GetPath(fixture));
            var matches = Regex.Matches(
                text,
                @"(?ms)^  - displayName: (.+?)\r?\n    testName: (.+?)\r?\n    family: (.+?)\r?\n    categories: (.+?)\r?\n    arguments:");
            Assert.NotEmpty(matches);
            foreach (Match scenario in matches)
            {
                var displayName = scenario.Groups[1].Value
                    .Trim()
                    .Trim('"');
                var testName = scenario.Groups[2].Value.Trim();
                var expectedFamily = scenario.Groups[3].Value.Trim();
                var expectedCategories = scenario.Groups[4].Value
                    .Split(',')
                    .Select(category => category.Trim())
                    .OrderBy(category => category, StringComparer.Ordinal)
                    .ToList();
                var row = BackfillTestData.CreateRow(
                    1001,
                    DateTimeOffset.Parse("2026-08-19T10:00:00Z"),
                    scenario: "legacy-scenario",
                    description:
                        $"{displayName} 8- Trends{(expectedTopology == "SUT+Load+DB" ? " Database" : string.Empty)} gold-lin",
                    documentScenario: "legacy-scenario");
                var resolved = LegacyTrendMappingMatcher.Resolve(
                    loaded.Mapping,
                    row,
                    LegacyJobResultsAdapter.Adapt(row));
                var identity = LegacyExportIdentityBuilder.Build(
                    row,
                    LegacyJobResultsAdapter.Adapt(row),
                    loaded.Mapping,
                    resolved,
                    loaded.Fingerprint,
                    "TrendBenchmarks",
                    BackfillTestData.CreateIdentityOptions());

                Assert.Equal(testName, identity.Scenario.Name);
                Assert.Equal(expectedFamily, identity.Scenario.Family);
                Assert.Equal(expectedCategories, identity.Scenario.Categories);
                Assert.Equal(
                    expectedTopology,
                    identity.Lane.Configurations["Topology"]);
            }
        }
    }
}
