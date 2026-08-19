// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Publishing;

namespace Crank.PerfLabExporter.CommandLine
{
    internal enum ExportMode
    {
        Convert,
        Upload,
        Backfill
    }

    internal enum IdentitySource
    {
        File,
        Crank
    }

    internal sealed class LiveIdentityOptions
    {
        public string PropertyPrefix { get; init; } = "perflab.";

        public string? RuntimeRepository { get; init; }

        public string? RuntimeBranch { get; init; }

        public string? RuntimeCommit { get; init; }

        public string? RuntimeBuildName { get; init; }

        public string? RuntimeVersion { get; init; }

        public string? RuntimeArtifactId { get; init; }

        public string? RuntimeCommitTimestamp { get; init; }

        public string? LaneName { get; init; }

        public string? LaneQueue { get; init; }

        public string? OsName { get; init; }

        public string? Architecture { get; init; }

        public string? Locale { get; init; }

        public string? MachineName { get; init; }

        public string? ScenarioName { get; init; }

        public string? ScenarioFamily { get; init; }

        public string? ScenarioCategories { get; init; }

        public string? PerfRepoHash { get; init; }

        public string? CrankVersion { get; init; }

        public string? CrankVersionEnvironmentVariable { get; init; }

        public string? AzureDevOpsProject { get; init; }

        public string? AzureDevOpsPipeline { get; init; }

        public string? AzureDevOpsBuildId { get; init; }

        public string? AzureDevOpsBuildNumber { get; init; }

        public string? AzureDevOpsBuildUrl { get; init; }

        public string? SqlSession { get; init; }

        public string? SqlTable { get; init; }

        public string? SqlRecordId { get; init; }

        public string? HelixCorrelationId { get; init; }
    }

    internal enum SqlAuthenticationMode
    {
        ConnectionString,
        DefaultAzureCredential,
        ManagedIdentity,
        Certificate,
        AccessToken
    }

    internal sealed class SqlAuthenticationOptions
    {
        public SqlAuthenticationMode Mode { get; init; }

        public string? AccessTokenEnvironmentVariable { get; init; }

        public StorageAuthenticationOptions AzureCredential { get; init; } = new();
    }

    internal sealed class BackfillIdentityOptions
    {
        public string RuntimeBranch { get; init; } = "main";

        public string BenchmarksCommit { get; init; } = string.Empty;

        public string CrankVersion { get; init; } = string.Empty;

        public string AzureDevOpsProject { get; init; } = string.Empty;

        public string AzureDevOpsPipeline { get; init; } = string.Empty;

        public string? AzureDevOpsBuildId { get; init; }

        public string? AzureDevOpsBuildNumber { get; init; }

        public string AzureDevOpsBuildUrlTemplate { get; init; } = string.Empty;

        public string? RuntimeArtifactId { get; init; }
    }

    internal sealed class BackfillOptions
    {
        public string ConnectionString { get; init; } = string.Empty;

        public string? ConnectionStringEnvironmentVariable { get; init; }

        public string Table { get; init; } = "TrendBenchmarks";

        public DateTimeOffset? StartUtc { get; init; }

        public DateTimeOffset? EndUtc { get; init; }

        public int BatchSize { get; init; } = 100;

        public int? MaximumRows { get; init; }

        public bool Publish { get; init; }

        public bool DryRun => !Publish;

        public string? PublicationConfirmation { get; init; }

        public string CounterPolicyPath { get; init; } =
            "crank-perflab-counter-policy.json";

        public string MappingPath { get; init; } =
            "trend-perflab-legacy-mapping.json";

        public string OutputDirectory { get; init; } = Environment.CurrentDirectory;

        public string CheckpointPath { get; init; } = string.Empty;

        public string SummaryPath { get; init; } = string.Empty;

        public string GitHubTokenEnvironmentVariable { get; init; } = "GITHUB_TOKEN";

        public BackfillIdentityOptions Identity { get; init; } = new();

        public SqlAuthenticationOptions SqlAuthentication { get; init; } = new();

        public PublicationRetryOptions SqlRetry { get; init; } =
            PublicationRetryOptions.Default;

        public string? StorageAccount { get; init; }

        public string? Container { get; init; }

        public string? Queue { get; init; }

        public StorageAuthenticationOptions Authentication { get; init; } = new();

        public PublicationRetryOptions Retry { get; init; } =
            PublicationRetryOptions.Default;
    }

    internal sealed class ExporterOptions
    {
        public ExportMode Mode { get; init; }

        public bool ShowHelp { get; init; }

        public string CrankJsonPath { get; init; } = string.Empty;

        public string CounterPolicyPath { get; init; } = string.Empty;

        public IdentitySource IdentitySource { get; init; }

        public string IdentityPath { get; init; } = string.Empty;

        public LiveIdentityOptions LiveIdentity { get; init; } = new();

        public string OutputDirectory { get; init; } = Environment.CurrentDirectory;

        public string GitHubTokenEnvironmentVariable { get; init; } = "GITHUB_TOKEN";

        public string? StorageAccount { get; init; }

        public string? Container { get; init; }

        public string? Queue { get; init; }

        public StorageAuthenticationOptions Authentication { get; init; } = new();

        public PublicationRetryOptions Retry { get; init; } = PublicationRetryOptions.Default;

        public BackfillOptions? Backfill { get; init; }
    }
}
