// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
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

            Required conversion options:
              --crank-json <path>       Crank --json execution result.
              --counter-policy <path>   Version-controlled counter mapping policy.
              --identity <path>         Runtime, lane, scenario, dependency, AzDO, and SQL identity.

            Conversion options:
              --output-directory <path>                  Output directory (default: current directory).
              --github-token-environment-variable <name> Environment variable containing an optional
                                                         GitHub token (default: GITHUB_TOKEN).

            Required upload options:
              --storage-account <name-or-uri>  Account name, blob/queue service URI, or URI template
                                               such as https://account.{}.core.windows.net.
              --container <name>               Results blob container.
              --queue <name>                   PerfLab ingestion queue (normally resultsqueue).

            Upload authentication options:
              --managed-identity-client-id <id>                 User-assigned managed identity client ID.
              --tenant-id <id>                                  Certificate credential tenant ID.
              --client-id <id>                                  Certificate credential client ID.
              --certificate-path <path>                          PFX/PEM certificate path.
              --certificate-base64-environment-variable <name>  Environment variable containing a
                                                                base64-encoded PFX certificate.
              --certificate-password-environment-variable <name>
                                                                Environment variable containing the
                                                                certificate password.

            Upload retry options:
              --maximum-attempts <count>       Attempts per blob/queue operation (default: 3).
              --retry-delay-seconds <seconds>  Initial exponential retry delay (default: 2).

            Authentication uses DefaultAzureCredential unless certificate inputs are supplied.
            Certificate passwords and GitHub tokens are read from environment variables and are never logged.
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
                _ => throw new ArgumentException(
                    $"Unknown command '{args[0]}'. Expected 'convert' or 'upload'.")
            };
            var values = ParseOptions(args[1..]);
            if (values.Remove("--help") || values.Remove("-h"))
            {
                return new ExporterOptions { Mode = mode, ShowHelp = true };
            }

            var crankJson = TakeRequired(values, "--crank-json");
            var counterPolicy = TakeRequired(values, "--counter-policy");
            var identity = TakeRequired(values, "--identity");
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
                authentication = new StorageAuthenticationOptions
                {
                    ManagedIdentityClientId = TakeOptional(values, "--managed-identity-client-id"),
                    TenantId = TakeOptional(values, "--tenant-id"),
                    ClientId = TakeOptional(values, "--client-id"),
                    CertificatePath = TakeOptional(values, "--certificate-path"),
                    CertificateBase64EnvironmentVariable =
                        TakeOptional(values, "--certificate-base64-environment-variable"),
                    CertificatePasswordEnvironmentVariable =
                        TakeOptional(values, "--certificate-password-environment-variable")
                };
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

                if (!string.IsNullOrWhiteSpace(authentication.CertificatePath) &&
                    !string.IsNullOrWhiteSpace(authentication.CertificateBase64EnvironmentVariable))
                {
                    throw new ArgumentException(
                        "Use either --certificate-path or --certificate-base64-environment-variable, not both.");
                }
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
                IdentityPath = identity,
                OutputDirectory = outputDirectory,
                GitHubTokenEnvironmentVariable = githubTokenEnvironmentVariable,
                StorageAccount = storageAccount,
                Container = container,
                Queue = queue,
                Authentication = authentication,
                Retry = retry
            };
        }

        private static Dictionary<string, string?> ParseOptions(string[] args)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                var option = args[index];
                if (option is "-h" or "--help")
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
    }
}
