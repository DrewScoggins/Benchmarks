// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Contracts.Crank;
using Crank.PerfLabExporter.Contracts.Identity;
using Crank.PerfLabExporter.Contracts.PerfLab;
using Crank.PerfLabExporter.Conversion;
using Crank.PerfLabExporter.Validation;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class LegacyIdentityResolutionException : Exception
    {
        public LegacyIdentityResolutionException(
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    internal static class LegacyExportIdentityBuilder
    {
        public static ExportIdentity Build(
            LegacyTrendRow row,
            CrankExecutionResult execution,
            LegacyTrendMapping mapping,
            LegacyTrendMappingMatch match,
            string mappingFingerprint,
            string table,
            BackfillIdentityOptions fallback)
        {
            var runtime = ResolveRole(
                execution,
                row.Id,
                "runtime",
                RepositoryIdentity.IsRuntimePackage,
                RepositoryIdentity.IsRuntimeRepository);
            var aspNetCore = ResolveRole(
                execution,
                row.Id,
                "ASP.NET Core",
                RepositoryIdentity.IsAspNetCorePackage,
                RepositoryIdentity.IsAspNetCoreRepository);
            var data = CreateTraceabilityData(
                row,
                match,
                mappingFingerprint,
                table);
            data["historical.known.runtime.repository"] = runtime.Repository;
            data["historical.known.runtime.commitHash"] = runtime.CommitHash;
            data["historical.known.runtime.version"] =
                runtime.Version ?? string.Empty;
            data["historical.known.aspnetCore.repository"] =
                aspNetCore.Repository;
            data["historical.known.aspnetCore.commitHash"] =
                aspNetCore.CommitHash;
            data["historical.known.aspnetCore.version"] =
                aspNetCore.Version ?? string.Empty;
            var scenarioTestName = ResolveScenarioTestName(
                row,
                execution,
                match.Scenario,
                data);
            var runtimeBranch = ResolveFallback(
                GetMetadataValue(execution, "runtime branch", "perflab.build.branch", "runtimeBranch"),
                fallback.RuntimeBranch,
                "runtimeBranch",
                data,
                row.Id);
            var perfRepoHash = ResolveFallback(
                GetMetadataValue(
                    execution,
                    "Benchmarks commit",
                    "perflab.perfRepoHash",
                    "benchmarksGitHash",
                    "benchmarksCommit",
                    "buildSourceVersion",
                    "BUILD_SOURCEVERSION"),
                fallback.BenchmarksCommit,
                "benchmarksCommit",
                data,
                row.Id);
            var crankVersion = ResolveFallback(
                GetCrankVersion(execution),
                fallback.CrankVersion,
                "crankVersion",
                data,
                row.Id);
            var buildId = ResolveFallback(
                GetMetadataValue(
                    execution,
                    "Azure DevOps build ID",
                    "perflab.azureDevOps.buildId",
                    "buildId",
                    "BUILD_BUILDID"),
                fallback.AzureDevOpsBuildId,
                "azureDevOpsBuildId",
                data,
                row.Id);
            var buildNumber = ResolveFallback(
                GetMetadataValue(
                    execution,
                    "Azure DevOps build number",
                    "perflab.azureDevOps.buildNumber",
                    "buildNumber",
                    "BUILD_BUILDNUMBER"),
                fallback.AzureDevOpsBuildNumber,
                "azureDevOpsBuildNumber",
                data,
                row.Id);
            var project = ResolveFallback(
                GetMetadataValue(
                    execution,
                    "Azure DevOps project",
                    "perflab.azureDevOps.project",
                    "SYSTEM_TEAMPROJECT"),
                fallback.AzureDevOpsProject,
                "azureDevOpsProject",
                data,
                row.Id);
            var pipeline = ResolveFallback(
                GetMetadataValue(
                    execution,
                    "Azure DevOps pipeline",
                    "perflab.azureDevOps.pipeline",
                    "BUILD_DEFINITIONNAME"),
                fallback.AzureDevOpsPipeline,
                "azureDevOpsPipeline",
                data,
                row.Id);
            var observedBuildUrl = GetMetadataValue(
                execution,
                "Azure DevOps build URL",
                "perflab.azureDevOps.buildUrl");
            var buildUrl = !string.IsNullOrWhiteSpace(observedBuildUrl)
                ? RecordKnown(data, "azureDevOpsBuildUrl", observedBuildUrl)
                : CreateBuildUrl(
                    fallback.AzureDevOpsBuildUrlTemplate,
                    buildId,
                    buildNumber,
                    data,
                    row.Id);

            var configurations = new SortedDictionary<string, string>(
                mapping.DefaultConfigurations,
                StringComparer.Ordinal);
            foreach (var configuration in configurations)
            {
                data[$"historical.mapping.configuration.{configuration.Key}"] =
                    configuration.Value;
            }

            OverrideConfiguration(
                configurations,
                "Framework",
                GetPrimaryMetadataValue(
                    execution,
                    "framework",
                    "framework",
                    "application.framework"),
                data);
            OverrideConfiguration(
                configurations,
                "Runtime",
                GetPrimaryMetadataValue(
                    execution,
                    "runtime",
                    "runtime",
                    "application.runtime"),
                data);
            OverrideConfiguration(
                configurations,
                "Configuration",
                GetPrimaryMetadataValue(
                    execution,
                    "configuration",
                    "configuration",
                    "application.configuration"),
                data);
            configurations["Cores"] =
                match.Lane.Cores.ToString(CultureInfo.InvariantCulture);
            configurations["Topology"] = match.Scenario.Topology;
            data["historical.mapping.configuration.Cores"] =
                configurations["Cores"];
            data["historical.mapping.configuration.Topology"] =
                configurations["Topology"];
            ValidateMappedEnvironment(execution, match.Lane, row.Id);

            var identity = new ExportIdentity
            {
                Build = new PrimaryBuildIdentity
                {
                    Repo = runtime.Repository,
                    Branch = runtimeBranch,
                    GitHash = runtime.CommitHash,
                    BuildName = !string.IsNullOrWhiteSpace(runtime.Version)
                        ? $"runtime-{runtime.Version}"
                        : $"runtime-{ShortHash(runtime.CommitHash)}",
                    TimeStamp = null,
                    Version = runtime.Version,
                    ArtifactId = fallback.RuntimeArtifactId
                },
                Lane = new LaneIdentity
                {
                    Name = match.Lane.Name,
                    Queue = match.Lane.Queue,
                    Os = new PerfLabOs
                    {
                        Name = match.Lane.Os,
                        Architecture = match.Lane.Architecture,
                        Locale = match.Lane.Locale,
                        MachineName = GetPrimaryMetadataValue(
                            execution,
                            "machine name",
                            "machineName",
                            "hostname",
                            "COMPUTERNAME")
                    },
                    Configurations = new Dictionary<string, string>(
                        configurations,
                        StringComparer.Ordinal),
                    AdditionalData =
                    {
                        ["hardware"] = match.Lane.Hardware,
                        ["historicalMappingRule"] = match.Lane.Id
                    }
                },
                Scenario = new ScenarioIdentity
                {
                    Name = scenarioTestName,
                    Family = match.Scenario.Family,
                    Categories = match.Scenario.Categories
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(category => category, StringComparer.Ordinal)
                        .ToList(),
                    AdditionalData =
                    {
                        ["historicalMappingRule"] = match.Scenario.Id,
                        ["legacySqlDescription"] = row.Description,
                        ["legacySqlScenario"] = row.Scenario,
                        ["legacySqlInsertionTimeUtc"] =
                            row.DateTimeUtc.ToUniversalTime().ToString("O")
                    }
                },
                Dependencies =
                [
                    CreateDependency("runtime", runtime, runtimeBranch),
                    CreateDependency("aspnetcore", aspNetCore, branch: null)
                ],
                PerfRepoHash = perfRepoHash,
                CrankVersion = crankVersion,
                HelixCorrelationId = GetPrimaryMetadataValue(
                    execution,
                    "Helix correlation ID",
                    "perflab.helixCorrelationId",
                    "helixCorrelationId"),
                AzureDevOps = new AzureDevOpsMetadata
                {
                    Project = project,
                    Pipeline = pipeline,
                    BuildId = buildId,
                    BuildNumber = buildNumber,
                    BuildUrl = buildUrl
                },
                Sql = new CrankSqlIdentity
                {
                    Session = Required(row.Session, "SQL session", row.Id),
                    Table = table,
                    RecordId = row.Id.ToString(CultureInfo.InvariantCulture)
                },
                AdditionalData = data
            };

            if (!string.IsNullOrWhiteSpace(fallback.RuntimeArtifactId))
            {
                identity.AdditionalData["historical.fallback.runtimeArtifactId"] =
                    fallback.RuntimeArtifactId;
            }

            try
            {
                ExportIdentityValidator.ValidateAndThrow(identity);
            }
            catch (ContractValidationException exception)
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {row.Id} produced invalid historical identity: {exception.Message}",
                    exception);
            }

            return identity;
        }

        private static DependencyFacts ResolveRole(
            CrankExecutionResult execution,
            long rowId,
            string role,
            Func<string?, bool> packageMatch,
            Func<string?, bool> repositoryMatch)
        {
            var dependencies = execution.JobResults.Jobs.Values
                .SelectMany(job => job.Dependencies)
                .ToList();
            var matches = dependencies
                .Where(dependency =>
                    dependency.Names.Append(dependency.Id).Any(packageMatch))
                .ToList();
            if (matches.Count == 0)
            {
                matches = dependencies
                    .Where(dependency => repositoryMatch(dependency.RepositoryUrl))
                    .ToList();
            }

            if (matches.Count == 0)
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} does not contain a normalized {role} dependency.");
            }

            var repositories = Distinct(
                matches.Select(match => match.RepositoryUrl),
                RepositoryIdentity.NormalizeRepository);
            var versions = Distinct(
                matches.Select(match => match.Version),
                value => value.Trim());
            var commits = Distinct(
                matches.Select(match => match.CommitHash),
                value => value.Trim().ToLowerInvariant());
            RejectConflicts(rowId, role, "repositories", repositories);
            RejectConflicts(rowId, role, "versions", versions);
            RejectConflicts(rowId, role, "commit hashes", commits);

            var repository = repositories.SingleOrDefault();
            var commit = commits.SingleOrDefault();
            if (string.IsNullOrWhiteSpace(repository) ||
                string.IsNullOrWhiteSpace(commit))
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} has incomplete normalized {role} dependency identity.");
            }

            return new DependencyFacts(
                matches
                    .SelectMany(match => match.Names.Append(match.Id))
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                RepositoryIdentity.ToCanonicalUrl(repository),
                versions.SingleOrDefault(),
                commit);
        }

        private static DependencyMetadata CreateDependency(
            string name,
            DependencyFacts facts,
            string? branch)
        {
            return new DependencyMetadata
            {
                Name = name,
                Aliases = facts.Aliases,
                Repository = facts.Repository,
                Branch = branch,
                Version = facts.Version,
                CommitHash = facts.CommitHash,
                CommitTimeStamp = null
            };
        }

        private static Dictionary<string, string> CreateTraceabilityData(
            LegacyTrendRow row,
            LegacyTrendMappingMatch match,
            string mappingFingerprint,
            string table)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["historical.known.sql.id"] =
                    row.Id.ToString(CultureInfo.InvariantCulture),
                ["historical.known.sql.session"] = row.Session,
                ["historical.known.sql.table"] = table,
                ["historical.known.sql.scenario"] = row.Scenario,
                ["historical.known.sql.description"] = row.Description,
                ["historical.known.sql.insertionTimeUtc"] =
                    row.DateTimeUtc.ToUniversalTime().ToString("O"),
                ["historical.mapping.fingerprint"] = mappingFingerprint,
                ["historical.mapping.laneRule"] = match.Lane.Id,
                ["historical.mapping.scenarioRule"] = match.Scenario.Id,
                ["historical.mapping.laneName"] = match.Lane.Name,
                ["historical.mapping.scenarioFamily"] = match.Scenario.Family,
                ["historical.source"] = "TrendBenchmarks"
            };
        }

        private static string ResolveFallback(
            string? knownValue,
            string? fallbackValue,
            string name,
            IDictionary<string, string> data,
            long rowId)
        {
            if (!string.IsNullOrWhiteSpace(knownValue))
            {
                return RecordKnown(data, name, knownValue);
            }

            if (string.IsNullOrWhiteSpace(fallbackValue))
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} has no known {name} and no operator fallback was configured.");
            }

            var value = fallbackValue.Trim();
            data[$"historical.fallback.{name}"] = value;
            return value;
        }

        private static string ResolveScenarioTestName(
            LegacyTrendRow row,
            CrankExecutionResult execution,
            LegacyScenarioRule scenario,
            IDictionary<string, string> data)
        {
            if (!scenario.TestName.Equals(
                    "$scenario",
                    StringComparison.Ordinal))
            {
                return scenario.TestName;
            }

            var propertyScenario = GetMetadataValue(
                execution,
                "canonical scenario",
                "scenario");
            if (TryGetCanonicalScenario(
                    propertyScenario,
                    scenario,
                    out var canonicalScenario))
            {
                return RecordKnown(
                    data,
                    "canonicalScenario",
                    canonicalScenario);
            }

            if (TryGetCanonicalScenario(
                    row.Scenario,
                    scenario,
                    out canonicalScenario))
            {
                data["historical.known.canonicalScenario"] = canonicalScenario;
                return canonicalScenario;
            }

            var descriptionMatches = scenario.DescriptionMatches
                .Where(description =>
                    LegacyTrendMappingMatcher.MatchesDescription(
                        row.Description,
                        description.Description))
                .ToList();
            if (descriptionMatches.Count == 1)
            {
                data["historical.mapping.description"] =
                    descriptionMatches[0].Description;
                data["historical.mapping.descriptionTestName"] =
                    descriptionMatches[0].TestName;
                return descriptionMatches[0].TestName;
            }

            if (descriptionMatches.Count > 1)
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {row.Id} has ambiguous description-to-test mapping: {string.Join(", ", descriptionMatches.Select(match => match.TestName))}.");
            }

            return Required(
                propertyScenario ?? row.Scenario,
                "canonical scenario",
                row.Id);
        }

        private static bool TryGetCanonicalScenario(
            string? value,
            LegacyScenarioRule scenario,
            out string canonicalScenario)
        {
            canonicalScenario = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var match = scenario.DescriptionMatches.FirstOrDefault(description =>
                description.TestName.Equals(
                    value,
                    StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return false;
            }

            canonicalScenario = match.TestName;
            return true;
        }

        private static string RecordKnown(
            IDictionary<string, string> data,
            string name,
            string value)
        {
            value = value.Trim();
            data[$"historical.known.{name}"] = value;
            return value;
        }

        private static string CreateBuildUrl(
            string template,
            string buildId,
            string buildNumber,
            IDictionary<string, string> data,
            long rowId)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} requires an Azure DevOps build URL template.");
            }

            var url = template
                .Replace("{buildId}", Uri.EscapeDataString(buildId), StringComparison.Ordinal)
                .Replace("{buildNumber}", Uri.EscapeDataString(buildNumber), StringComparison.Ordinal);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                throw new LegacyIdentityResolutionException(
                    $"The configured Azure DevOps build URL template produced an invalid URL for legacy SQL row {rowId}.");
            }

            data["historical.fallback.azureDevOpsBuildUrlTemplate"] = template;
            return url;
        }

        private static void OverrideConfiguration(
            IDictionary<string, string> configurations,
            string name,
            string? observed,
            IDictionary<string, string> data)
        {
            if (!string.IsNullOrWhiteSpace(observed))
            {
                configurations[name] = observed.Trim();
                data.Remove($"historical.mapping.configuration.{name}");
                data[$"historical.known.configuration.{name}"] = observed.Trim();
            }
        }

        private static void ValidateMappedEnvironment(
            CrankExecutionResult execution,
            LegacyLaneRule lane,
            long rowId)
        {
            var architecture = GetPrimaryMetadataValue(
                execution,
                "architecture",
                "architecture",
                "processArchitecture");
            if (!string.IsNullOrWhiteSpace(architecture) &&
                !string.Equals(
                    NormalizeArchitecture(architecture),
                    NormalizeArchitecture(lane.Architecture),
                    StringComparison.Ordinal))
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} mapped lane architecture '{lane.Architecture}' contradicts primary job architecture '{architecture}'.");
            }

            var locale = GetPrimaryMetadataValue(
                execution,
                "locale",
                "locale",
                "culture");
            if (!string.IsNullOrWhiteSpace(locale) &&
                !string.Equals(
                    locale,
                    lane.Locale,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} mapped lane locale '{lane.Locale}' contradicts primary job locale '{locale}'.");
            }
        }

        private static string? GetCrankVersion(CrankExecutionResult execution)
        {
            var values = new[]
            {
                execution.CrankVersion,
                GetMetadataValue(execution, "Crank version", "crankVersion", "CRANK_VERSION")
            };
            return ResolveDistinct(values, "Crank version");
        }

        private static string? GetMetadataValue(
            CrankExecutionResult execution,
            string description,
            params string[] names)
        {
            var values = new List<string?>();
            foreach (var name in names)
            {
                values.AddRange(execution.JobResults.Properties
                    .Where(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Value));
                foreach (var job in execution.JobResults.Jobs.Values)
                {
                    values.AddRange(GetElementValues(job.Environment, name));
                    values.AddRange(GetElementValues(job.Variables, name));
                }
            }

            return ResolveDistinct(values, description);
        }

        private static string? GetPrimaryMetadataValue(
            CrankExecutionResult execution,
            string description,
            params string[] names)
        {
            var values = new List<string?>();
            var primaryJob = GetPrimaryJob(execution);
            foreach (var name in names)
            {
                values.AddRange(execution.JobResults.Properties
                    .Where(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Value));
                if (primaryJob is not null)
                {
                    values.AddRange(GetElementValues(primaryJob.Environment, name));
                    values.AddRange(GetElementValues(primaryJob.Variables, name));
                }
            }

            return ResolveDistinct(values, description);
        }

        private static CrankJobResult? GetPrimaryJob(
            CrankExecutionResult execution)
        {
            return execution.JobResults.Jobs
                .OrderBy(pair =>
                    pair.Key.Equals(
                        "application",
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : pair.Value.Dependencies.Any(dependency =>
                            dependency.Names
                                .Append(dependency.Id)
                                .Any(RepositoryIdentity.IsRuntimePackage))
                            ? 1
                            : 2)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .FirstOrDefault();
        }

        private static IEnumerable<string?> GetElementValues(
            IReadOnlyDictionary<string, JsonElement> values,
            string name)
        {
            foreach (var pair in values.Where(pair =>
                pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                pair.Key.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)))
            {
                yield return pair.Value.ValueKind switch
                {
                    JsonValueKind.String => pair.Value.GetString(),
                    JsonValueKind.Number or
                    JsonValueKind.True or
                    JsonValueKind.False => pair.Value.GetRawText(),
                    _ => null
                };
            }
        }

        private static string? ResolveDistinct(
            IEnumerable<string?> values,
            string description)
        {
            var distinct = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count > 1)
            {
                throw new LegacyIdentityResolutionException(
                    $"Conflicting historical {description} values were found: {string.Join(", ", distinct)}.");
            }

            return distinct.SingleOrDefault();
        }

        private static List<string> Distinct(
            IEnumerable<string?> values,
            Func<string, string> normalize)
        {
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => normalize(value!))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void RejectConflicts(
            long rowId,
            string role,
            string field,
            IReadOnlyCollection<string> values)
        {
            if (values.Count > 1)
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} has conflicting normalized {role} {field}: {string.Join(", ", values)}.");
            }
        }

        private static string Required(
            string value,
            string description,
            long rowId)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new LegacyIdentityResolutionException(
                    $"Legacy SQL row {rowId} has an empty {description}.");
            }

            return value.Trim();
        }

        private static string ShortHash(string hash)
        {
            return hash.Length <= 12 ? hash : hash[..12];
        }

        private static string NormalizeArchitecture(string architecture)
        {
            return architecture.Trim().ToLowerInvariant() switch
            {
                "amd64" => "x64",
                "aarch64" => "arm64",
                var value => value
            };
        }

        private sealed record DependencyFacts(
            List<string> Aliases,
            string Repository,
            string? Version,
            string CommitHash);
    }
}
