// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Conversion;

namespace Crank.PerfLabExporter.Tests
{
    public class LiveExportIdentityBuilderTests
    {
        [Fact]
        public void BuildsIdentityFromCrankPropertiesDependenciesAndEnvironment()
        {
            var identity = LiveExportIdentityBuilder.Build(
                FixtureLoader.LoadExecution(),
                new LiveIdentityOptions());

            Assert.Equal("https://github.com/dotnet/runtime", identity.Build.Repo);
            Assert.Equal("main", identity.Build.Branch);
            Assert.Equal(
                "1111111111111111111111111111111111111111",
                identity.Build.GitHash);
            Assert.Equal(
                "runtime-10.0.0-preview.7.25380.103",
                identity.Build.BuildName);
            Assert.Equal("aspnet-gold-linux-x64", identity.Lane.Name);
            Assert.Equal(
                "Ubuntu.2204.Amd64.AspNetGold.Perf",
                identity.Lane.Queue);
            Assert.Equal("CoreCLR", identity.Lane.Configurations["Runtime"]);
            Assert.Equal("SUT+Load", identity.Lane.Configurations["Topology"]);
            Assert.Equal("Plaintext", identity.Scenario.Name);
            Assert.Equal("aspnet-plaintext", identity.Scenario.Family);
            Assert.Equal(["aspnet", "plaintext"], identity.Scenario.Categories);
            Assert.Equal("12345", identity.AzureDevOps.BuildId);
            Assert.Equal("TrendBenchmarks", identity.Sql.Table);
            Assert.Equal("TrendBenchmarks", identity.AdditionalData["producer"]);
            Assert.Contains(
                identity.Dependencies,
                dependency =>
                    dependency.Name == "runtime" &&
                    dependency.Branch == "main");
        }

        [Fact]
        public void ExplicitOptionsOverrideNamespacedProperties()
        {
            var identity = LiveExportIdentityBuilder.Build(
                FixtureLoader.LoadExecution(),
                new LiveIdentityOptions
                {
                    LaneName = "override-lane",
                    ScenarioFamily = "override-family",
                    ScenarioCategories = "one;two"
                });

            Assert.Equal("override-lane", identity.Lane.Name);
            Assert.Equal("override-family", identity.Scenario.Family);
            Assert.Equal(["one", "two"], identity.Scenario.Categories);
        }

        [Fact]
        public void RejectsMissingRequiredRunConfiguration()
        {
            var execution = FixtureLoader.LoadExecution();
            execution.JobResults.Properties.Remove(
                "perflab.configuration.Topology");

            var exception = Assert.Throws<CrankConversionException>(() =>
                LiveExportIdentityBuilder.Build(
                    execution,
                    new LiveIdentityOptions()));

            Assert.Contains(
                "perflab.configuration.Topology",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUnexpandedPipelineExpressions()
        {
            var execution = FixtureLoader.LoadExecution();
            execution.JobResults.Properties["perflab.azureDevOps.project"] =
                "$(System.TeamProject)";

            var exception = Assert.Throws<CrankConversionException>(() =>
                LiveExportIdentityBuilder.Build(
                    execution,
                    new LiveIdentityOptions()));

            Assert.Contains(
                "unexpanded pipeline expression",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsLaneArchitectureThatContradictsApplicationEnvironment()
        {
            var execution = FixtureLoader.LoadExecution();
            execution.JobResults.Properties[
                "perflab.lane.os.architecture"] = "arm64";

            var exception = Assert.Throws<CrankConversionException>(() =>
                LiveExportIdentityBuilder.Build(
                    execution,
                    new LiveIdentityOptions()));

            Assert.Contains(
                "contradicts application environment architecture",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void AcceptsRealCrankAgentEnvironmentShapeWhenLaneMatches()
        {
            var execution = FixtureLoader.LoadExecution();
            var environment =
                execution.JobResults.Jobs["application"].Environment;

            Assert.Equal("X64", environment["arch"].GetString());
            Assert.Equal(0, environment["os"].GetInt32());
            Assert.True(environment.ContainsKey("hw"));
            Assert.True(environment.ContainsKey("env"));

            var identity = LiveExportIdentityBuilder.Build(
                execution,
                new LiveIdentityOptions());

            Assert.Equal("x64", identity.Lane.Os.Architecture);
            Assert.Equal("Ubuntu 22.04", identity.Lane.Os.Name);
        }

        [Fact]
        public void RejectsLaneOperatingSystemThatContradictsApplicationEnvironment()
        {
            var execution = FixtureLoader.LoadExecution();
            execution.JobResults.Properties["perflab.lane.os.name"] =
                "Windows Server 2022";

            var exception = Assert.Throws<CrankConversionException>(() =>
                LiveExportIdentityBuilder.Build(
                    execution,
                    new LiveIdentityOptions()));

            Assert.Contains(
                "contradicts application environment operating system",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void PreservesLegacyEnvironmentArchitectureAndOperatingSystemAliases()
        {
            var execution = FixtureLoader.LoadExecution();
            execution.JobResults.Jobs["application"].Environment =
                new Dictionary<string, JsonElement>
                {
                    ["processArchitecture"] =
                        JsonSerializer.SerializeToElement("AMD64"),
                    ["operatingSystem"] =
                        JsonSerializer.SerializeToElement("Linux")
                };

            var identity = LiveExportIdentityBuilder.Build(
                execution,
                new LiveIdentityOptions());

            Assert.Equal("x64", identity.Lane.Os.Architecture);
            Assert.Equal("Ubuntu 22.04", identity.Lane.Os.Name);
        }
    }
}
