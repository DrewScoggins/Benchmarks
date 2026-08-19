// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Data.SqlClient;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class BackfillSummary
    {
        public int SchemaVersion { get; set; } = 1;

        public DateTimeOffset StartUtc { get; set; }

        public DateTimeOffset EndUtc { get; set; }

        public string Table { get; set; } = string.Empty;

        public bool DryRun { get; set; }

        public string MappingFingerprint { get; set; } = string.Empty;

        public string ConfigurationFingerprint { get; set; } = string.Empty;

        public int Scanned { get; set; }

        public int Excluded { get; set; }

        public int Converted { get; set; }

        public int Uploaded { get; set; }

        public int DryRunValidated { get; set; }

        public int Unresolved { get; set; }

        public int Failed { get; set; }

        public List<BackfillIssue> Issues { get; set; } = [];
    }

    internal sealed record BackfillIssue(
        long? RowId,
        string Kind,
        string Reason);

    internal interface ISecretRedactor
    {
        string Redact(string value);
    }

    internal sealed class SecretRedactor : ISecretRedactor
    {
        private readonly IReadOnlyList<string> _secrets;

        private SecretRedactor(IReadOnlyList<string> secrets)
        {
            _secrets = secrets;
        }

        public static SecretRedactor Create(params string?[] sensitiveValues)
        {
            var secrets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sensitiveValue in sensitiveValues.Where(value =>
                !string.IsNullOrWhiteSpace(value)))
            {
                secrets.Add(sensitiveValue!);
                try
                {
                    var builder = new SqlConnectionStringBuilder(sensitiveValue);
                    if (!string.IsNullOrWhiteSpace(builder.Password))
                    {
                        secrets.Add(builder.Password);
                    }
                }
                catch (ArgumentException)
                {
                }

                if (Uri.TryCreate(sensitiveValue, UriKind.Absolute, out var uri) &&
                    !string.IsNullOrWhiteSpace(uri.Query))
                {
                    secrets.Add(uri.Query);
                    foreach (var parameter in uri.Query.TrimStart('?').Split('&'))
                    {
                        var separator = parameter.IndexOf('=');
                        if (separator >= 0 && separator < parameter.Length - 1)
                        {
                            secrets.Add(
                                Uri.UnescapeDataString(parameter[(separator + 1)..]));
                        }
                    }
                }
            }

            return new SecretRedactor(
                secrets.OrderByDescending(secret => secret.Length).ToList());
        }

        public string Redact(string value)
        {
            foreach (var secret in _secrets)
            {
                value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }

            const int maximumLength = 1000;
            return value.Length <= maximumLength
                ? value
                : value[..maximumLength] + "...";
        }
    }

    internal sealed class NullSecretRedactor : ISecretRedactor
    {
        public string Redact(string value)
        {
            return value;
        }
    }
}
