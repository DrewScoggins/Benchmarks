// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using Crank.PerfLabExporter.Backfill;
using Crank.PerfLabExporter.Publishing;

namespace Crank.PerfLabExporter.CommandLine
{
    internal static class ExporterCommandLine
    {
        public const string Help =
            """
            Converts Crank --json output to canonical PerfLab JSON and optionally publishes it.

            Usage:
              Crank.PerfLabExporter convert [options]
              Crank.PerfLabExporter upload  [options]
              Crank.PerfLabExporter backfill [options]

            Required conversion options:
              --crank-json <path>       Crank --json execution result.
              --counter-policy <path>   Version-controlled counter mapping policy.

            Identity options (choose one):
              --identity <path>            Read identity from a JSON file (file conversion mode).
              --identity-source crank      Build live identity from perflab.* Crank properties,
                                           raw dependencies, and raw job environment data.

            Conversion options:
              --output-directory <path>                  Output directory (default: current directory).
              --github-token-environment-variable <name> Environment variable containing an optional
                                                         GitHub token (default: GITHUB_TOKEN).

            Live identity options:
              --identity-property-prefix <prefix>         Crank property namespace (default: perflab.).
              --runtime-repository <repo>                 Override runtime repository.
              --runtime-branch <branch>                   Override runtime branch.
              --runtime-commit <hash>                     Override runtime commit.
              --runtime-build-name <name>                 Override runtime build name.
              --runtime-version <version>                 Override runtime version.
              --runtime-artifact-id <id>                  Override runtime artifact ID.
              --runtime-commit-timestamp <timestamp>      Override runtime commit timestamp.
              --lane-name|--lane-queue <value>            Override stable lane identity.
              --os-name|--architecture|--locale <value>   Override operating-system identity.
              --machine-name <value>                      Override machine name.
              --scenario-name|--scenario-family <value>   Override test/family identity.
              --scenario-categories <csv>                 Override scenario categories.
              --perf-repo-hash <hash>                     Override the Benchmarks commit.
              --crank-version <version>                   Override the Crank version.
              --crank-version-environment-variable <name> Read Crank version from this environment
                                                         variable when no explicit/property value exists.
              --azdo-project|--azdo-pipeline <value>      Override Azure DevOps identity.
              --azdo-build-id|--azdo-build-number <value> Override Azure DevOps build identity.
              --azdo-build-url <url>                      Override Azure DevOps build URL.
              --sql-session|--sql-table|--sql-record-id   Override SQL identity.
              --helix-correlation-id <guid>               Override Helix correlation identity.

            Required upload options:
              --storage-account <name-or-uri>  Account name, blob/queue service URI, or URI template
                                               such as https://account.{}.core.windows.net.
              --container <name>               Results blob container.
              --queue <name>                   PerfLab ingestion queue (normally resultsqueue).

            Upload authentication options:
              --storage-authentication <mode>                       default (default), managed-identity,
                                                                    or certificate.
              --managed-identity-client-id <id>                 User-assigned managed identity client ID.
              --managed-identity-client-id-environment-variable <name>
                                                                Environment variable containing it.
              --tenant-id <id>                                  Certificate credential tenant ID.
              --tenant-id-environment-variable <name>           Environment variable containing it.
              --client-id <id>                                  Certificate credential client ID.
              --client-id-environment-variable <name>           Environment variable containing it.
              --certificate-path <path>                          PFX/PEM certificate path.
              --certificate-path-environment-variable <name>    Environment variable containing its path.
              --certificate-base64-environment-variable <name>  Environment variable containing a
                                                                base64-encoded PFX certificate.
              --certificate-password-environment-variable <name>
                                                                Environment variable containing the
                                                                certificate password.

            Upload retry options:
              --maximum-attempts <count>       Attempts per blob/queue operation (default: 3).
              --retry-delay-seconds <seconds>  Initial exponential retry delay (default: 2).

            Legacy Trend backfill:
              --sql-connection-string <value>                  Direct SQL connection string.
              --sql-connection-string-environment-variable <name>
                                                              Environment variable containing it.
              --sql-table <identifier>                         Safely quoted table or schema.table
                                                              (default: TrendBenchmarks).
              --start-utc|--end-utc <timestamp>                Inclusive UTC bounds (default: latest
                                                              90 days, fixed in the checkpoint).
              --batch-size <count>                             SQL page size (default: 100).
              --maximum-rows <count>                           Optional maximum scanned rows.
              --dry-run|--convert-only                         Explicit convert-only mode (also the default).
              --publish                                       Explicitly opt in to live blob/queue publication.
              --confirm-live-publication PUBLISH_TREND_BACKFILL
                                                              Required exact confirmation with --publish.
              --checkpoint <path>                              Atomic resume checkpoint.
              --summary <path>                                 Machine-readable summary JSON.
              --legacy-mapping <path>                          Ordered lane/scenario rules (default:
                                                              trend-perflab-legacy-mapping.json).
              --counter-policy <path>                          Counter policy (default:
                                                              crank-perflab-counter-policy.json).
              --benchmarks-commit <hash>                       Benchmarks fallback commit.
              --crank-version <version>                        Crank fallback version.
              --runtime-branch <branch>                        Runtime branch fallback (default: main).
              --azdo-project|--azdo-pipeline <value>           Azure DevOps fallback identity.
              --azdo-build-id|--azdo-build-number <value>      Optional per-row fallback values.
              --azdo-build-url-template <template>             URL with optional {buildId} and
                                                              {buildNumber} placeholders.

            Backfill SQL authentication:
              --sql-authentication <mode>                      connection-string (default), default,
                                                              managed-identity, certificate, or token.
              --sql-managed-identity-client-id <id>            User-assigned managed identity ID.
              --sql-managed-identity-client-id-environment-variable <name>
              --sql-tenant-id|--sql-client-id <id>             Certificate identity.
              --sql-tenant-id-environment-variable <name>
              --sql-client-id-environment-variable <name>
              --sql-certificate-path <path>
              --sql-certificate-path-environment-variable <name>
              --sql-certificate-base64-environment-variable <name>
              --sql-certificate-password-environment-variable <name>
              --sql-access-token-environment-variable <name>   Token mode secret source.
              --sql-maximum-attempts <count>                   SQL attempts (default: 3).
              --sql-retry-delay-seconds <seconds>              SQL retry delay (default: 2).

            Default storage authentication uses DefaultAzureCredential. Managed identity and certificate
            modes use only their explicitly selected credential type.
            Credential secrets and GitHub tokens are read from environment variables and are never logged.
            Backfill defaults to dry-run. Storage options alone never enable publication.
            Blob uploads overwrite the deterministic name; rerunning the same export is idempotent.
            """;

        public static ExporterOptions Parse(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                return new ExporterOptions { ShowHelp = true };
            }

            var mode = args[0] switch
            {
                "convert" => ExportMode.Convert,
                "upload" => ExportMode.Upload,
                "backfill" => ExportMode.Backfill,
                _ => throw new ArgumentException(
                    $"Unknown command '{args[0]}'. Expected 'convert', 'upload', or 'backfill'.")
            };
            var values = ParseOptions(args[1..]);
            if (values.Remove("--help") || values.Remove("-h"))
            {
                return new ExporterOptions { Mode = mode, ShowHelp = true };
            }

            if (mode == ExportMode.Backfill)
            {
                return ParseBackfill(values);
            }

            var crankJson = TakeRequired(values, "--crank-json");
            var counterPolicy = TakeRequired(values, "--counter-policy");
            var identityPath = TakeOptional(values, "--identity");
            var identitySource = ParseIdentitySource(
                TakeOptional(values, "--identity-source"),
                identityPath);
            var liveIdentity = identitySource == IdentitySource.Crank
                ? ParseLiveIdentity(values)
                : new LiveIdentityOptions();
            if (identitySource == IdentitySource.File && string.IsNullOrWhiteSpace(identityPath))
            {
                throw new ArgumentException(
                    "Supply '--identity <path>' or use '--identity-source crank'.");
            }

            var outputDirectory = TakeOptional(values, "--output-directory") ?? Environment.CurrentDirectory;
            var githubTokenEnvironmentVariable =
                TakeOptional(values, "--github-token-environment-variable") ?? "GITHUB_TOKEN";

            string? storageAccount = null;
            string? container = null;
            string? queue = null;
            var authentication = new StorageAuthenticationOptions();
            var retry = PublicationRetryOptions.Default;
            if (mode == ExportMode.Upload)
            {
                storageAccount = TakeRequired(values, "--storage-account");
                container = TakeRequired(values, "--container");
                queue = TakeRequired(values, "--queue");
                authentication = ParseStorageAuthentication(values);
                var maximumAttempts = ParsePositiveInteger(
                    TakeOptional(values, "--maximum-attempts"),
                    PublicationRetryOptions.Default.MaximumAttempts,
                    "--maximum-attempts");
                var retryDelaySeconds = ParseNonNegativeDouble(
                    TakeOptional(values, "--retry-delay-seconds"),
                    PublicationRetryOptions.Default.InitialDelay.TotalSeconds,
                    "--retry-delay-seconds");
                retry = new PublicationRetryOptions(
                    maximumAttempts,
                    TimeSpan.FromSeconds(retryDelaySeconds));
            }

            if (values.Count > 0)
            {
                throw new ArgumentException($"Unknown option '{values.Keys.Order(StringComparer.Ordinal).First()}'.");
            }

            return new ExporterOptions
            {
                Mode = mode,
                CrankJsonPath = crankJson,
                CounterPolicyPath = counterPolicy,
                IdentitySource = identitySource,
                IdentityPath = identityPath ?? string.Empty,
                LiveIdentity = liveIdentity,
                OutputDirectory = outputDirectory,
                GitHubTokenEnvironmentVariable = githubTokenEnvironmentVariable,
                StorageAccount = storageAccount,
                Container = container,
                Queue = queue,
                Authentication = authentication,
                Retry = retry
            };
        }

        private static ExporterOptions ParseBackfill(
            IDictionary<string, string?> values)
        {
            var directConnectionString =
                TakeOptional(values, "--sql-connection-string");
            var connectionStringEnvironmentVariable =
                TakeOptional(
                    values,
                    "--sql-connection-string-environment-variable");
            ValidateExactlyOne(
                directConnectionString,
                connectionStringEnvironmentVariable,
                "--sql-connection-string",
                "--sql-connection-string-environment-variable");
            var table = SqlTableIdentifier.Parse(
                TakeOptional(values, "--sql-table") ?? "TrendBenchmarks");
            var startUtc = ParseUtcTimestamp(
                TakeOptional(values, "--start-utc"),
                "--start-utc");
            var endUtc = ParseUtcTimestamp(
                TakeOptional(values, "--end-utc"),
                "--end-utc");
            if (startUtc is not null &&
                endUtc is not null &&
                startUtc > endUtc)
            {
                throw new ArgumentException(
                    "Option '--start-utc' must not be later than '--end-utc'.");
            }

            var batchSize = ParsePositiveInteger(
                TakeOptional(values, "--batch-size"),
                100,
                "--batch-size");
            var maximumRowsValue = TakeOptional(values, "--maximum-rows");
            int? maximumRows = maximumRowsValue is null
                ? null
                : ParsePositiveInteger(
                    maximumRowsValue,
                    defaultValue: 1,
                    "--maximum-rows");
            var explicitDryRun =
                values.Remove("--dry-run") |
                values.Remove("--convert-only");
            var publish = values.Remove("--publish");
            if (publish && explicitDryRun)
            {
                throw new ArgumentException(
                    "Use either --publish or --dry-run/--convert-only, not both.");
            }

            var publicationConfirmation =
                TakeOptional(values, "--confirm-live-publication");
            BackfillPublicationSafety.Validate(
                publish,
                publicationConfirmation);
            var dryRun = !publish;
            var outputDirectory =
                TakeOptional(values, "--output-directory") ??
                Environment.CurrentDirectory;
            var checkpointPath =
                TakeOptional(values, "--checkpoint") ??
                Path.Combine(outputDirectory, "trend-backfill.checkpoint.json");
            var summaryPath =
                TakeOptional(values, "--summary") ??
                Path.Combine(outputDirectory, "trend-backfill-summary.json");
            var counterPolicy =
                TakeOptional(values, "--counter-policy") ??
                "crank-perflab-counter-policy.json";
            var mappingPath =
                TakeOptional(values, "--legacy-mapping") ??
                "trend-perflab-legacy-mapping.json";
            var githubTokenEnvironmentVariable =
                TakeOptional(values, "--github-token-environment-variable") ??
                "GITHUB_TOKEN";

            var sqlAuthenticationMode = ParseSqlAuthenticationMode(
                TakeOptional(values, "--sql-authentication"));
            var sqlAzureCredential = new StorageAuthenticationOptions
            {
                Mode = sqlAuthenticationMode switch
                {
                    SqlAuthenticationMode.ManagedIdentity =>
                        StorageAuthenticationMode.ManagedIdentity,
                    SqlAuthenticationMode.Certificate =>
                        StorageAuthenticationMode.Certificate,
                    _ => StorageAuthenticationMode.Default
                },
                ManagedIdentityClientId =
                    TakeOptional(values, "--sql-managed-identity-client-id"),
                ManagedIdentityClientIdEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--sql-managed-identity-client-id-environment-variable"),
                TenantId = TakeOptional(values, "--sql-tenant-id"),
                TenantIdEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--sql-tenant-id-environment-variable"),
                ClientId = TakeOptional(values, "--sql-client-id"),
                ClientIdEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--sql-client-id-environment-variable"),
                CertificatePath =
                    TakeOptional(values, "--sql-certificate-path"),
                CertificatePathEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--sql-certificate-path-environment-variable"),
                CertificateBase64EnvironmentVariable =
                    TakeOptional(
                        values,
                        "--sql-certificate-base64-environment-variable"),
                CertificatePasswordEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--sql-certificate-password-environment-variable")
            };
            var sqlAccessTokenEnvironmentVariable =
                TakeOptional(
                    values,
                    "--sql-access-token-environment-variable");
            ValidateSqlAuthentication(
                sqlAuthenticationMode,
                sqlAzureCredential,
                sqlAccessTokenEnvironmentVariable);
            var sqlMaximumAttempts = ParsePositiveInteger(
                TakeOptional(values, "--sql-maximum-attempts"),
                PublicationRetryOptions.Default.MaximumAttempts,
                "--sql-maximum-attempts");
            var sqlRetryDelaySeconds = ParseNonNegativeDouble(
                TakeOptional(values, "--sql-retry-delay-seconds"),
                PublicationRetryOptions.Default.InitialDelay.TotalSeconds,
                "--sql-retry-delay-seconds");

            string? storageAccount;
            string? container;
            string? queue;
            if (dryRun)
            {
                storageAccount = TakeOptional(values, "--storage-account");
                container = TakeOptional(values, "--container");
                queue = TakeOptional(values, "--queue");
            }
            else
            {
                storageAccount = TakeRequired(values, "--storage-account");
                container = TakeRequired(values, "--container");
                queue = TakeRequired(values, "--queue");
            }

            var authentication = ParseStorageAuthentication(values);
            var maximumAttempts = ParsePositiveInteger(
                TakeOptional(values, "--maximum-attempts"),
                PublicationRetryOptions.Default.MaximumAttempts,
                "--maximum-attempts");
            var retryDelaySeconds = ParseNonNegativeDouble(
                TakeOptional(values, "--retry-delay-seconds"),
                PublicationRetryOptions.Default.InitialDelay.TotalSeconds,
                "--retry-delay-seconds");

            var identity = new BackfillIdentityOptions
            {
                RuntimeBranch =
                    TakeOptional(values, "--runtime-branch") ?? "main",
                BenchmarksCommit =
                    TakeRequired(values, "--benchmarks-commit"),
                CrankVersion = TakeRequired(values, "--crank-version"),
                AzureDevOpsProject =
                    TakeRequired(values, "--azdo-project"),
                AzureDevOpsPipeline =
                    TakeRequired(values, "--azdo-pipeline"),
                AzureDevOpsBuildId =
                    TakeOptional(values, "--azdo-build-id"),
                AzureDevOpsBuildNumber =
                    TakeOptional(values, "--azdo-build-number"),
                AzureDevOpsBuildUrlTemplate =
                    TakeRequired(values, "--azdo-build-url-template"),
                RuntimeArtifactId =
                    TakeOptional(values, "--runtime-artifact-id")
            };

            if (values.Count > 0)
            {
                throw new ArgumentException(
                    $"Unknown option '{values.Keys.Order(StringComparer.Ordinal).First()}'.");
            }

            return new ExporterOptions
            {
                Mode = ExportMode.Backfill,
                Backfill = new BackfillOptions
                {
                    ConnectionString = directConnectionString ?? string.Empty,
                    ConnectionStringEnvironmentVariable =
                        connectionStringEnvironmentVariable,
                    Table = table.CanonicalName,
                    StartUtc = startUtc,
                    EndUtc = endUtc,
                    BatchSize = batchSize,
                    MaximumRows = maximumRows,
                    Publish = publish,
                    PublicationConfirmation = publicationConfirmation,
                    CounterPolicyPath = counterPolicy,
                    MappingPath = mappingPath,
                    OutputDirectory = outputDirectory,
                    CheckpointPath = checkpointPath,
                    SummaryPath = summaryPath,
                    GitHubTokenEnvironmentVariable =
                        githubTokenEnvironmentVariable,
                    Identity = identity,
                    SqlAuthentication = new SqlAuthenticationOptions
                    {
                        Mode = sqlAuthenticationMode,
                        AccessTokenEnvironmentVariable =
                            sqlAccessTokenEnvironmentVariable,
                        AzureCredential = sqlAzureCredential
                    },
                    SqlRetry = new PublicationRetryOptions(
                        sqlMaximumAttempts,
                        TimeSpan.FromSeconds(sqlRetryDelaySeconds)),
                    StorageAccount = storageAccount,
                    Container = container,
                    Queue = queue,
                    Authentication = authentication,
                    Retry = new PublicationRetryOptions(
                        maximumAttempts,
                        TimeSpan.FromSeconds(retryDelaySeconds))
                }
            };
        }

        private static IdentitySource ParseIdentitySource(
            string? value,
            string? identityPath)
        {
            if (value is null)
            {
                return IdentitySource.File;
            }

            var source = value.ToLowerInvariant() switch
            {
                "file" => IdentitySource.File,
                "crank" => IdentitySource.Crank,
                _ => throw new ArgumentException(
                    $"Unknown identity source '{value}'. Expected 'file' or 'crank'.")
            };
            if (source == IdentitySource.Crank && !string.IsNullOrWhiteSpace(identityPath))
            {
                throw new ArgumentException(
                    "Use either '--identity <path>' or '--identity-source crank', not both.");
            }

            return source;
        }

        private static LiveIdentityOptions ParseLiveIdentity(IDictionary<string, string?> values)
        {
            return new LiveIdentityOptions
            {
                PropertyPrefix =
                    TakeOptional(values, "--identity-property-prefix") ?? "perflab.",
                RuntimeRepository = TakeOptional(values, "--runtime-repository"),
                RuntimeBranch = TakeOptional(values, "--runtime-branch"),
                RuntimeCommit = TakeOptional(values, "--runtime-commit"),
                RuntimeBuildName = TakeOptional(values, "--runtime-build-name"),
                RuntimeVersion = TakeOptional(values, "--runtime-version"),
                RuntimeArtifactId = TakeOptional(values, "--runtime-artifact-id"),
                RuntimeCommitTimestamp =
                    TakeOptional(values, "--runtime-commit-timestamp"),
                LaneName = TakeOptional(values, "--lane-name"),
                LaneQueue = TakeOptional(values, "--lane-queue"),
                OsName = TakeOptional(values, "--os-name"),
                Architecture = TakeOptional(values, "--architecture"),
                Locale = TakeOptional(values, "--locale"),
                MachineName = TakeOptional(values, "--machine-name"),
                ScenarioName = TakeOptional(values, "--scenario-name"),
                ScenarioFamily = TakeOptional(values, "--scenario-family"),
                ScenarioCategories = TakeOptional(values, "--scenario-categories"),
                PerfRepoHash = TakeOptional(values, "--perf-repo-hash"),
                CrankVersion = TakeOptional(values, "--crank-version"),
                CrankVersionEnvironmentVariable =
                    TakeOptional(values, "--crank-version-environment-variable"),
                AzureDevOpsProject = TakeOptional(values, "--azdo-project"),
                AzureDevOpsPipeline = TakeOptional(values, "--azdo-pipeline"),
                AzureDevOpsBuildId = TakeOptional(values, "--azdo-build-id"),
                AzureDevOpsBuildNumber = TakeOptional(values, "--azdo-build-number"),
                AzureDevOpsBuildUrl = TakeOptional(values, "--azdo-build-url"),
                SqlSession = TakeOptional(values, "--sql-session"),
                SqlTable = TakeOptional(values, "--sql-table"),
                SqlRecordId = TakeOptional(values, "--sql-record-id"),
                HelixCorrelationId = TakeOptional(values, "--helix-correlation-id")
            };
        }

        private static Dictionary<string, string?> ParseOptions(string[] args)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                var option = args[index];
                if (option is "-h" or "--help" or "--dry-run" or "--convert-only" or "--publish")
                {
                    if (!values.TryAdd(option, null))
                    {
                        throw new ArgumentException($"Option '{option}' was supplied more than once.");
                    }

                    continue;
                }

                if (!option.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{option}'.");
                }

                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Option '{option}' requires a value.");
                }

                if (!values.TryAdd(option, args[++index]))
                {
                    throw new ArgumentException($"Option '{option}' was supplied more than once.");
                }
            }

            return values;
        }

        private static DateTimeOffset? ParseUtcTimestamp(
            string? value,
            string option)
        {
            if (value is null)
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces |
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
            {
                throw new ArgumentException(
                    $"Option '{option}' must be a valid UTC timestamp.");
            }

            return timestamp.ToUniversalTime();
        }

        private static SqlAuthenticationMode ParseSqlAuthenticationMode(
            string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                null or "connection-string" =>
                    SqlAuthenticationMode.ConnectionString,
                "default" =>
                    SqlAuthenticationMode.DefaultAzureCredential,
                "managed-identity" =>
                    SqlAuthenticationMode.ManagedIdentity,
                "certificate" =>
                    SqlAuthenticationMode.Certificate,
                "token" =>
                    SqlAuthenticationMode.AccessToken,
                _ => throw new ArgumentException(
                    $"Unknown SQL authentication mode '{value}'. Expected connection-string, default, managed-identity, certificate, or token.")
            };
        }

        private static StorageAuthenticationOptions ParseStorageAuthentication(
            IDictionary<string, string?> values)
        {
            var options = new StorageAuthenticationOptions
            {
                Mode = ParseStorageAuthenticationMode(
                    TakeOptional(values, "--storage-authentication")),
                ManagedIdentityClientId =
                    TakeOptional(values, "--managed-identity-client-id"),
                ManagedIdentityClientIdEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--managed-identity-client-id-environment-variable"),
                TenantId = TakeOptional(values, "--tenant-id"),
                TenantIdEnvironmentVariable =
                    TakeOptional(values, "--tenant-id-environment-variable"),
                ClientId = TakeOptional(values, "--client-id"),
                ClientIdEnvironmentVariable =
                    TakeOptional(values, "--client-id-environment-variable"),
                CertificatePath = TakeOptional(values, "--certificate-path"),
                CertificatePathEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--certificate-path-environment-variable"),
                CertificateBase64EnvironmentVariable =
                    TakeOptional(
                        values,
                        "--certificate-base64-environment-variable"),
                CertificatePasswordEnvironmentVariable =
                    TakeOptional(
                        values,
                        "--certificate-password-environment-variable")
            };
            ValidateStorageAuthentication(options);
            return options;
        }

        private static StorageAuthenticationMode ParseStorageAuthenticationMode(
            string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                null or "default" => StorageAuthenticationMode.Default,
                "managed-identity" =>
                    StorageAuthenticationMode.ManagedIdentity,
                "certificate" => StorageAuthenticationMode.Certificate,
                _ => throw new ArgumentException(
                    $"Unknown storage authentication mode '{value}'. Expected default, managed-identity, or certificate.")
            };
        }

        private static void ValidateStorageAuthentication(
            StorageAuthenticationOptions options)
        {
            ValidateAzureCredentialOptions(options, "--");
            var hasManagedIdentity =
                HasValue(options.ManagedIdentityClientId) ||
                HasValue(options.ManagedIdentityClientIdEnvironmentVariable);
            var hasTenant =
                HasValue(options.TenantId) ||
                HasValue(options.TenantIdEnvironmentVariable);
            var hasClient =
                HasValue(options.ClientId) ||
                HasValue(options.ClientIdEnvironmentVariable);
            var hasCertificateMaterial =
                HasValue(options.CertificatePath) ||
                HasValue(options.CertificatePathEnvironmentVariable) ||
                HasValue(options.CertificateBase64EnvironmentVariable);
            var hasCertificateOptions =
                hasTenant ||
                hasClient ||
                hasCertificateMaterial ||
                HasValue(options.CertificatePasswordEnvironmentVariable);

            switch (options.Mode)
            {
                case StorageAuthenticationMode.Default:
                    if (hasManagedIdentity || hasCertificateOptions)
                    {
                        throw new ArgumentException(
                            "Storage default authentication does not accept managed identity or certificate options. Use --storage-authentication managed-identity or certificate.");
                    }

                    break;
                case StorageAuthenticationMode.ManagedIdentity:
                    if (hasCertificateOptions)
                    {
                        throw new ArgumentException(
                            "Storage managed-identity authentication accepts only --managed-identity-client-id or --managed-identity-client-id-environment-variable.");
                    }

                    break;
                case StorageAuthenticationMode.Certificate:
                    if (hasManagedIdentity)
                    {
                        throw new ArgumentException(
                            "Storage certificate authentication cannot be combined with managed identity options.");
                    }

                    var missing = new List<string>();
                    if (!hasTenant)
                    {
                        missing.Add(
                            "--tenant-id or --tenant-id-environment-variable");
                    }

                    if (!hasClient)
                    {
                        missing.Add(
                            "--client-id or --client-id-environment-variable");
                    }

                    if (!hasCertificateMaterial)
                    {
                        missing.Add(
                            "--certificate-path, --certificate-path-environment-variable, or --certificate-base64-environment-variable");
                    }

                    if (missing.Count > 0)
                    {
                        throw new ArgumentException(
                            $"Storage certificate authentication requires {string.Join(", ", missing)}.");
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(options),
                        options.Mode,
                        "Unknown storage authentication mode.");
            }
        }

        private static void ValidateSqlAuthentication(
            SqlAuthenticationMode mode,
            StorageAuthenticationOptions azure,
            string? accessTokenEnvironmentVariable)
        {
            ValidateAzureCredentialOptions(azure, "--sql-");
            var hasManagedIdentity =
                HasValue(azure.ManagedIdentityClientId) ||
                HasValue(azure.ManagedIdentityClientIdEnvironmentVariable);
            var hasTenant =
                HasValue(azure.TenantId) ||
                HasValue(azure.TenantIdEnvironmentVariable);
            var hasClient =
                HasValue(azure.ClientId) ||
                HasValue(azure.ClientIdEnvironmentVariable);
            var hasCertificateMaterial =
                HasValue(azure.CertificatePath) ||
                HasValue(azure.CertificatePathEnvironmentVariable) ||
                HasValue(azure.CertificateBase64EnvironmentVariable);
            var hasCertificateOptions =
                hasTenant ||
                hasClient ||
                hasCertificateMaterial ||
                HasValue(azure.CertificatePasswordEnvironmentVariable);
            var hasAzureOptions =
                hasManagedIdentity ||
                hasCertificateOptions;
            var hasAccessToken =
                HasValue(accessTokenEnvironmentVariable);

            switch (mode)
            {
                case SqlAuthenticationMode.ConnectionString:
                    if (hasAzureOptions || hasAccessToken)
                    {
                        throw new ArgumentException(
                            "SQL connection-string authentication cannot be combined with SQL Azure credential or token options.");
                    }

                    break;
                case SqlAuthenticationMode.AccessToken:
                    if (!hasAccessToken)
                    {
                        throw new ArgumentException(
                            "SQL token authentication requires --sql-access-token-environment-variable.");
                    }

                    if (hasAzureOptions)
                    {
                        throw new ArgumentException(
                            "SQL token authentication cannot be combined with SQL Azure credential options.");
                    }

                    break;
                case SqlAuthenticationMode.Certificate:
                    if (hasManagedIdentity)
                    {
                        throw new ArgumentException(
                            "SQL certificate authentication cannot be combined with SQL managed identity options.");
                    }

                    if (hasAccessToken)
                    {
                        throw new ArgumentException(
                            "SQL certificate authentication cannot be combined with a SQL access token.");
                    }

                    var missing = new List<string>();
                    if (!hasTenant)
                    {
                        missing.Add(
                            "--sql-tenant-id or --sql-tenant-id-environment-variable");
                    }

                    if (!hasClient)
                    {
                        missing.Add(
                            "--sql-client-id or --sql-client-id-environment-variable");
                    }

                    if (!hasCertificateMaterial)
                    {
                        missing.Add(
                            "--sql-certificate-path, --sql-certificate-path-environment-variable, or --sql-certificate-base64-environment-variable");
                    }

                    if (missing.Count > 0)
                    {
                        throw new ArgumentException(
                            $"SQL certificate authentication requires {string.Join(", ", missing)}.");
                    }

                    break;
                case SqlAuthenticationMode.DefaultAzureCredential:
                    if (hasAzureOptions)
                    {
                        throw new ArgumentException(
                            "SQL default authentication does not accept managed identity or certificate options. Use --sql-authentication managed-identity or certificate.");
                    }

                    if (hasAccessToken)
                    {
                        throw new ArgumentException(
                            "SQL default authentication cannot be combined with a SQL access token.");
                    }

                    break;
                case SqlAuthenticationMode.ManagedIdentity:
                    if (hasCertificateOptions)
                    {
                        throw new ArgumentException(
                            "SQL managed-identity authentication accepts only --sql-managed-identity-client-id or --sql-managed-identity-client-id-environment-variable.");
                    }

                    if (hasAccessToken)
                    {
                        throw new ArgumentException(
                            "SQL managed-identity authentication cannot be combined with a SQL access token.");
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mode),
                        mode,
                        "Unknown SQL authentication mode.");
            }
        }

        private static void ValidateAzureCredentialOptions(
            StorageAuthenticationOptions options,
            string prefix)
        {
            if (HasValue(options.CertificatePath) &&
                HasValue(options.CertificatePathEnvironmentVariable))
            {
                throw new ArgumentException(
                    $"Use either {prefix}certificate-path or {prefix}certificate-path-environment-variable, not both.");
            }

            if ((HasValue(options.CertificatePath) ||
                 HasValue(options.CertificatePathEnvironmentVariable)) &&
                HasValue(options.CertificateBase64EnvironmentVariable))
            {
                throw new ArgumentException(
                    $"Use either a {prefix}certificate path or {prefix}certificate-base64-environment-variable, not both.");
            }

            ValidateDirectOrEnvironment(
                options.ManagedIdentityClientId,
                options.ManagedIdentityClientIdEnvironmentVariable,
                $"{prefix}managed-identity-client-id",
                $"{prefix}managed-identity-client-id-environment-variable");
            ValidateDirectOrEnvironment(
                options.TenantId,
                options.TenantIdEnvironmentVariable,
                $"{prefix}tenant-id",
                $"{prefix}tenant-id-environment-variable");
            ValidateDirectOrEnvironment(
                options.ClientId,
                options.ClientIdEnvironmentVariable,
                $"{prefix}client-id",
                $"{prefix}client-id-environment-variable");
            ValidateDirectOrEnvironment(
                options.CertificatePath,
                options.CertificatePathEnvironmentVariable,
                $"{prefix}certificate-path",
                $"{prefix}certificate-path-environment-variable");
        }

        private static bool HasValue(string? value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        private static void ValidateExactlyOne(
            string? firstValue,
            string? secondValue,
            string firstOption,
            string secondOption)
        {
            if (string.IsNullOrWhiteSpace(firstValue) ==
                string.IsNullOrWhiteSpace(secondValue))
            {
                throw new ArgumentException(
                    $"Supply exactly one of {firstOption} or {secondOption}.");
            }
        }

        private static string TakeRequired(IDictionary<string, string?> values, string option)
        {
            var value = TakeOptional(values, option);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Required option '{option}' was not supplied.");
            }

            return value;
        }

        private static string? TakeOptional(IDictionary<string, string?> values, string option)
        {
            if (!values.Remove(option, out var value))
            {
                return null;
            }

            return value;
        }

        private static int ParsePositiveInteger(string? value, int defaultValue, string option)
        {
            if (value is null)
            {
                return defaultValue;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed <= 0)
            {
                throw new ArgumentException($"Option '{option}' must be a positive integer.");
            }

            return parsed;
        }

        private static double ParseNonNegativeDouble(
            string? value,
            double defaultValue,
            string option)
        {
            if (value is null)
            {
                return defaultValue;
            }

            if (!double.TryParse(
                    value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsed) ||
                !double.IsFinite(parsed) ||
                parsed < 0)
            {
                throw new ArgumentException($"Option '{option}' must be a finite non-negative number.");
            }

            return parsed;
        }

        private static void ValidateDirectOrEnvironment(
            string? directValue,
            string? environmentVariable,
            string directOption,
            string environmentOption)
        {
            if (!string.IsNullOrWhiteSpace(directValue) &&
                !string.IsNullOrWhiteSpace(environmentVariable))
            {
                throw new ArgumentException(
                    $"Use either {directOption} or {environmentOption}, not both.");
            }
        }
    }
}
