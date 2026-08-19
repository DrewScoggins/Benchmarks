// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Contracts.Crank;
using Crank.PerfLabExporter.Contracts.Identity;
using Crank.PerfLabExporter.Contracts.PerfLab;
using Crank.PerfLabExporter.Validation;

namespace Crank.PerfLabExporter.Conversion
{
    internal static class LiveExportIdentityBuilder
    {
        private static readonly string[] RequiredConfigurations =
        [
            "Framework",
            "Runtime",
            "Cores",
            "Topology"
        ];

        public static ExportIdentity Build(
            CrankExecutionResult execution,
            LiveIdentityOptions options)
        {
            var properties = new NamespacedProperties(
                execution.JobResults.Properties,
                NormalizePrefix(options.PropertyPrefix));
            var runtime = ResolveRole(
                execution,
                "runtime",
                RepositoryIdentity.IsRuntimePackage,
                RepositoryIdentity.IsRuntimeRepository,
                required: true)!;
            var aspNetCore = ResolveRole(
                execution,
                "aspnetcore",
                RepositoryIdentity.IsAspNetCorePackage,
                RepositoryIdentity.IsAspNetCoreRepository,
                required: false);
            var primaryEnvironment = GetPrimaryEnvironment(execution);

            var runtimeRepository = Required(
                Select(options.RuntimeRepository, properties.Get("build.repo"), runtime.Repository),
                "runtime repository");
            var runtimeBranch = Required(
                Select(options.RuntimeBranch, properties.Get("build.branch")),
                "runtime branch");
            var runtimeCommit = Required(
                Select(options.RuntimeCommit, properties.Get("build.gitHash"), runtime.CommitHash),
                "runtime commit");
            var runtimeVersion = Select(
                options.RuntimeVersion,
                properties.Get("build.version"),
                runtime.Version);
            var runtimeTimestamp = ParseTimestamp(
                Select(
                    options.RuntimeCommitTimestamp,
                    properties.Get("build.timeStamp")),
                "runtime commit timestamp");
            var runtimeBuildName = Select(
                options.RuntimeBuildName,
                properties.Get("build.name"));
            if (string.IsNullOrWhiteSpace(runtimeBuildName))
            {
                runtimeBuildName = !string.IsNullOrWhiteSpace(runtimeVersion)
                    ? $"runtime-{runtimeVersion}"
                    : $"runtime-{ShortHash(runtimeCommit)}";
            }

            var configurations = properties.GetChildren("configuration.");
            foreach (var configuration in RequiredConfigurations)
            {
                if (!configurations.TryGetValue(configuration, out var value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    throw new CrankConversionException(
                        $"Live identity requires Crank property " +
                        $"'{properties.Prefix}configuration.{configuration}'.");
                }
            }

            var crankVersion = Select(
                options.CrankVersion,
                properties.Get("crankVersion"),
                execution.CrankVersion);
            if (string.IsNullOrWhiteSpace(crankVersion))
            {
                crankVersion = ReadOptionalEnvironmentVariable(
                    options.CrankVersionEnvironmentVariable,
                    "Crank version");
            }
            var scenarioName = Select(
                options.ScenarioName,
                properties.Get("scenario.name"),
                GetCrankProperty(execution, "scenario"));
            var azureDevOpsBuildId = Select(
                options.AzureDevOpsBuildId,
                properties.Get("azureDevOps.buildId"),
                GetCrankProperty(execution, "buildId"));
            var azureDevOpsBuildNumber = Select(
                options.AzureDevOpsBuildNumber,
                properties.Get("azureDevOps.buildNumber"),
                GetCrankProperty(execution, "buildNumber"));

            var identity = new ExportIdentity
            {
                Build = new PrimaryBuildIdentity
                {
                    Repo = runtimeRepository,
                    Branch = runtimeBranch,
                    GitHash = runtimeCommit,
                    BuildName = runtimeBuildName,
                    TimeStamp = runtimeTimestamp,
                    Version = runtimeVersion,
                    ArtifactId = Select(
                        options.RuntimeArtifactId,
                        properties.Get("build.artifactId")),
                    AdditionalData = properties.GetChildren("build.additionalData.")
                },
                Lane = new LaneIdentity
                {
                    Name = Required(
                        Select(options.LaneName, properties.Get("lane.name")),
                        "lane name"),
                    Queue = Required(
                        Select(options.LaneQueue, properties.Get("lane.queue")),
                        "PerfLab lane queue"),
                    Os = new PerfLabOs
                    {
                        Name = Required(
                            Select(
                                options.OsName,
                                properties.Get("lane.os.name"),
                                GetEnvironmentValue(
                                    primaryEnvironment,
                                    "os",
                                    "operatingSystem",
                                    "osDescription")),
                            "operating-system name"),
                        Architecture = Required(
                            Select(
                                options.Architecture,
                                properties.Get("lane.os.architecture"),
                                GetEnvironmentValue(
                                    primaryEnvironment,
                                    "arch",
                                    "architecture",
                                    "processArchitecture")),
                            "architecture"),
                        Locale = Required(
                            Select(
                                options.Locale,
                                properties.Get("lane.os.locale"),
                                GetEnvironmentValue(
                                    primaryEnvironment,
                                    "locale",
                                    "culture")),
                            "locale"),
                        MachineName = Select(
                            options.MachineName,
                            properties.Get("lane.os.machineName"),
                            GetEnvironmentValue(
                                primaryEnvironment,
                                "machineName",
                                "hostname"))
                    },
                    Configurations = configurations,
                    Hidden = ParseBoolean(
                        properties.Get("lane.hidden"),
                        "lane hidden flag"),
                    AdditionalData = properties.GetChildren("lane.additionalData.")
                },
                Scenario = new ScenarioIdentity
                {
                    Name = Required(scenarioName, "scenario name"),
                    Family = Required(
                        Select(
                            options.ScenarioFamily,
                            properties.Get("scenario.family")),
                        "scenario family"),
                    Categories = ParseCategories(
                        Select(
                            options.ScenarioCategories,
                            properties.Get("scenario.categories"))),
                    AdditionalData =
                        properties.GetChildren("scenario.additionalData.")
                },
                Dependencies = CreateDependencyMetadata(
                    runtime,
                    aspNetCore,
                    runtimeRepository,
                    runtimeBranch,
                    runtimeCommit,
                    runtimeVersion,
                    runtimeTimestamp,
                    properties),
                PerfRepoHash = Required(
                    Select(
                        options.PerfRepoHash,
                        properties.Get("perfRepoHash")),
                    "Benchmarks commit"),
                CrankVersion = Required(crankVersion, "Crank version"),
                HelixCorrelationId = Select(
                    options.HelixCorrelationId,
                    properties.Get("helixCorrelationId")),
                AzureDevOps = new AzureDevOpsMetadata
                {
                    Project = Required(
                        Select(
                            options.AzureDevOpsProject,
                            properties.Get("azureDevOps.project")),
                        "Azure DevOps project"),
                    Pipeline = Required(
                        Select(
                            options.AzureDevOpsPipeline,
                            properties.Get("azureDevOps.pipeline")),
                        "Azure DevOps pipeline"),
                    BuildId = Required(
                        azureDevOpsBuildId,
                        "Azure DevOps build ID"),
                    BuildNumber = Required(
                        azureDevOpsBuildNumber,
                        "Azure DevOps build number"),
                    BuildUrl = Required(
                        Select(
                            options.AzureDevOpsBuildUrl,
                            properties.Get("azureDevOps.buildUrl")),
                        "Azure DevOps build URL")
                },
                Sql = new CrankSqlIdentity
                {
                    Session = Required(
                        Select(
                            options.SqlSession,
                            properties.Get("sql.session")),
                        "SQL session"),
                    Table = Select(options.SqlTable, properties.Get("sql.table")),
                    RecordId = Select(
                        options.SqlRecordId,
                        properties.Get("sql.recordId"))
                },
                AdditionalData = properties.GetChildren("additionalData.")
            };

            ValidateApplicationEnvironment(identity, primaryEnvironment);
            ExportIdentityValidator.ValidateAndThrow(identity);
            return identity;
        }

        private static void ValidateApplicationEnvironment(
            ExportIdentity identity,
            IReadOnlyDictionary<string, JsonElement> environment)
        {
            var observedArchitecture = GetEnvironmentValue(
                environment,
                "arch",
                "architecture",
                "processArchitecture");
            if (!string.IsNullOrWhiteSpace(observedArchitecture) &&
                !string.Equals(
                    NormalizeArchitecture(identity.Lane.Os.Architecture),
                    NormalizeArchitecture(observedArchitecture),
                    StringComparison.Ordinal))
            {
                throw new CrankConversionException(
                    $"Declared lane architecture '{identity.Lane.Os.Architecture}' " +
                    $"contradicts application environment architecture " +
                    $"'{observedArchitecture}'.");
            }

            var observedOperatingSystem = GetEnvironmentValue(
                environment,
                "os",
                "operatingSystem",
                "osDescription");
            if (!string.IsNullOrWhiteSpace(observedOperatingSystem) &&
                !string.Equals(
                    NormalizeOperatingSystem(identity.Lane.Os.Name),
                    NormalizeOperatingSystem(observedOperatingSystem),
                    StringComparison.Ordinal))
            {
                throw new CrankConversionException(
                    $"Declared lane operating system '{identity.Lane.Os.Name}' " +
                    $"contradicts application environment operating system " +
                    $"'{observedOperatingSystem}'.");
            }

            var observedLocale = GetEnvironmentValue(
                environment,
                "locale",
                "culture");
            if (!string.IsNullOrWhiteSpace(observedLocale) &&
                !string.Equals(
                    identity.Lane.Os.Locale,
                    observedLocale,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CrankConversionException(
                    $"Declared lane locale '{identity.Lane.Os.Locale}' " +
                    $"contradicts application environment locale " +
                    $"'{observedLocale}'.");
            }
        }

        private static List<DependencyMetadata> CreateDependencyMetadata(
            DependencyFacts runtime,
            DependencyFacts? aspNetCore,
            string runtimeRepository,
            string runtimeBranch,
            string runtimeCommit,
            string? runtimeVersion,
            DateTimeOffset? runtimeTimestamp,
            NamespacedProperties properties)
        {
            var dependencies = new List<DependencyMetadata>
            {
                new()
                {
                    Name = "runtime",
                    Aliases = runtime.Aliases,
                    Repository = runtimeRepository,
                    Branch = runtimeBranch,
                    Version = runtimeVersion,
                    CommitHash = runtimeCommit,
                    CommitTimeStamp = runtimeTimestamp
                }
            };
            if (aspNetCore is not null)
            {
                dependencies.Add(new DependencyMetadata
                {
                    Name = "aspnetcore",
                    Aliases = aspNetCore.Aliases,
                    Repository = aspNetCore.Repository,
                    Branch = properties.Get("dependency.aspnetcore.branch"),
                    Version = aspNetCore.Version,
                    CommitHash = aspNetCore.CommitHash
                });
            }

            return dependencies;
        }

        private static DependencyFacts? ResolveRole(
            CrankExecutionResult execution,
            string role,
            Func<string?, bool> packageMatch,
            Func<string?, bool> repositoryMatch,
            bool required)
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
                if (!required)
                {
                    return null;
                }

                throw new CrankConversionException(
                    $"The Crank result does not contain a normalized {role} dependency.");
            }

            var repositories = DistinctValues(
                matches.Select(match => match.RepositoryUrl),
                RepositoryIdentity.NormalizeRepository);
            var versions = DistinctValues(
                matches.Select(match => match.Version),
                value => value.Trim());
            var commits = DistinctValues(
                matches.Select(match => match.CommitHash),
                value => value.Trim().ToLowerInvariant());
            ThrowIfConflicting(role, "repositories", repositories);
            ThrowIfConflicting(role, "versions", versions);
            ThrowIfConflicting(role, "commit hashes", commits);

            var aliases = matches
                .SelectMany(match => match.Names.Append(match.Id))
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new DependencyFacts(
                aliases,
                CanonicalRepository(repositories.SingleOrDefault()),
                versions.SingleOrDefault(),
                commits.SingleOrDefault());
        }

        private static Dictionary<string, JsonElement> GetPrimaryEnvironment(
            CrankExecutionResult execution)
        {
            var job = execution.JobResults.Jobs
                .OrderBy(pair =>
                    pair.Key.Equals("application", StringComparison.OrdinalIgnoreCase)
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
            return job?.Environment ?? [];
        }

        private static string? GetEnvironmentValue(
            IReadOnlyDictionary<string, JsonElement> environment,
            params string[] names)
        {
            foreach (var name in names)
            {
                var match = environment.FirstOrDefault(pair =>
                    pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match.Key))
                {
                    return match.Value.ValueKind switch
                    {
                        JsonValueKind.String => match.Value.GetString(),
                        JsonValueKind.Number or
                        JsonValueKind.True or
                        JsonValueKind.False => match.Value.GetRawText(),
                        _ => null
                    };
                }
            }

            return null;
        }

        private static string? GetCrankProperty(
            CrankExecutionResult execution,
            string name)
        {
            var property = execution.JobResults.Properties.FirstOrDefault(pair =>
                pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(property.Key) ? null : property.Value;
        }

        private static List<string> ParseCategories(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? []
                : value
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(category => category.Trim())
                    .Where(category => category.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(category => category, StringComparer.Ordinal)
                    .ToList();
        }

        private static DateTimeOffset? ParseTimestamp(
            string? value,
            string description)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
            {
                throw new CrankConversionException(
                    $"The {description} '{value}' is not a valid timestamp.");
            }

            return timestamp;
        }

        private static bool ParseBoolean(string? value, string description)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!bool.TryParse(value, out var result))
            {
                throw new CrankConversionException(
                    $"The {description} '{value}' is not true or false.");
            }

            return result;
        }

        private static string? ReadOptionalEnvironmentVariable(
            string? name,
            string description)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CrankConversionException(
                    $"The {description} environment variable '{name}' is not set.");
            }

            return value;
        }

        private static string Required(string? value, string description)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CrankConversionException(
                    $"Live identity requires a non-empty {description}.");
            }

            RejectUnexpandedValue(value, description);
            return value.Trim();
        }

        private static string? Select(params string?[] values)
        {
            var value = values.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate));
            if (value is not null)
            {
                RejectUnexpandedValue(value, "identity value");
            }

            return value?.Trim();
        }

        private static void RejectUnexpandedValue(string value, string description)
        {
            if (value.Contains("$(", StringComparison.Ordinal) ||
                value.Contains("${{", StringComparison.Ordinal))
            {
                throw new CrankConversionException(
                    $"The {description} contains an unexpanded pipeline expression '{value}'.");
            }
        }

        private static string NormalizePrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new CrankConversionException(
                    "The live identity property prefix cannot be empty.");
            }

            prefix = prefix.Trim();
            return prefix.EndsWith(".", StringComparison.Ordinal)
                ? prefix
                : prefix + ".";
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

        private static string NormalizeOperatingSystem(string operatingSystem)
        {
            var value = operatingSystem.Trim().ToLowerInvariant();
            // Crank's Newtonsoft endpoint emits its Linux/Windows/OSX enum as 0/1/2.
            if (value == "0")
            {
                return "linux";
            }

            if (value == "1")
            {
                return "windows";
            }

            if (value == "2")
            {
                return "osx";
            }

            if (value.Contains("windows", StringComparison.Ordinal))
            {
                return "windows";
            }

            if (value.Contains("linux", StringComparison.Ordinal) ||
                value.Contains("ubuntu", StringComparison.Ordinal))
            {
                return "linux";
            }

            if (value.Contains("osx", StringComparison.Ordinal) ||
                value.Contains("macos", StringComparison.Ordinal) ||
                value.Contains("mac os", StringComparison.Ordinal) ||
                value.Contains("darwin", StringComparison.Ordinal))
            {
                return "osx";
            }

            return value;
        }

        private static List<string> DistinctValues(
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

        private static void ThrowIfConflicting(
            string role,
            string field,
            IReadOnlyCollection<string> values)
        {
            if (values.Count > 1)
            {
                throw new CrankConversionException(
                    $"Conflicting {role} {field} were found: {string.Join(", ", values)}.");
            }
        }

        private static string CanonicalRepository(string? repository)
        {
            return string.IsNullOrWhiteSpace(repository)
                ? string.Empty
                : RepositoryIdentity.ToCanonicalUrl(repository);
        }

        private sealed record DependencyFacts(
            List<string> Aliases,
            string Repository,
            string? Version,
            string? CommitHash);

        private sealed class NamespacedProperties
        {
            private readonly Dictionary<string, string> _values =
                new(StringComparer.OrdinalIgnoreCase);

            public NamespacedProperties(
                IReadOnlyDictionary<string, string> properties,
                string prefix)
            {
                Prefix = prefix;
                foreach (var property in properties)
                {
                    if (!property.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = property.Key[prefix.Length..];
                    if (name.Length == 0)
                    {
                        throw new CrankConversionException(
                            $"Crank property '{property.Key}' has no identity key.");
                    }

                    if (_values.TryGetValue(name, out var existing) &&
                        !string.Equals(existing, property.Value, StringComparison.Ordinal))
                    {
                        throw new CrankConversionException(
                            $"Crank identity property '{property.Key}' is duplicated " +
                            "with conflicting casing or values.");
                    }

                    RejectUnexpandedValue(property.Value, $"Crank property '{property.Key}'");
                    _values[name] = property.Value.Trim();
                }
            }

            public string Prefix { get; }

            public string? Get(string name)
            {
                return _values.TryGetValue(name, out var value) &&
                    !string.IsNullOrWhiteSpace(value)
                    ? value
                    : null;
            }

            public Dictionary<string, string> GetChildren(string prefix)
            {
                return _values
                    .Where(pair =>
                        pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        pair.Key.Length > prefix.Length &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        pair => pair.Key[prefix.Length..],
                        pair => pair.Value,
                        StringComparer.Ordinal);
            }
        }
    }
}
