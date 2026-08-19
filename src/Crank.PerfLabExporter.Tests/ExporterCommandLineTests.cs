// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Backfill;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Publishing;

namespace Crank.PerfLabExporter.Tests
{
    public class ExporterCommandLineTests
    {
        [Fact]
        public void KeepsIdentityFileModeAsDefault()
        {
            var options = ExporterCommandLine.Parse(
            [
                "convert",
                "--crank-json", "crank.json",
                "--counter-policy", "policy.json",
                "--identity", "identity.json"
            ]);

            Assert.Equal(IdentitySource.File, options.IdentitySource);
            Assert.Equal("identity.json", options.IdentityPath);
        }

        [Fact]
        public void ParsesLiveIdentityAndCredentialEnvironmentReferences()
        {
            var options = ExporterCommandLine.Parse(
            [
                "upload",
                "--crank-json", "crank.json",
                "--counter-policy", "policy.json",
                "--identity-source", "crank",
                "--identity-property-prefix", "trend.",
                "--crank-version-environment-variable", "CRANK_VERSION",
                "--storage-account", "account",
                "--container", "results",
                "--queue", "resultsqueue",
                "--storage-authentication", "certificate",
                "--tenant-id-environment-variable", "TENANT_ID",
                "--client-id-environment-variable", "CLIENT_ID",
                "--certificate-base64-environment-variable", "CERTIFICATE",
                "--certificate-password-environment-variable", "CERTIFICATE_PASSWORD"
            ]);

            Assert.Equal(IdentitySource.Crank, options.IdentitySource);
            Assert.Equal("trend.", options.LiveIdentity.PropertyPrefix);
            Assert.Equal(
                "CRANK_VERSION",
                options.LiveIdentity.CrankVersionEnvironmentVariable);
            Assert.Equal(
                StorageAuthenticationMode.Certificate,
                options.Authentication.Mode);
            Assert.Equal(
                "TENANT_ID",
                options.Authentication.TenantIdEnvironmentVariable);
            Assert.Equal(
                "CLIENT_ID",
                options.Authentication.ClientIdEnvironmentVariable);
        }

        [Fact]
        public void RejectsIdentityFileAndLiveModeTogether()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                [
                    "convert",
                    "--crank-json", "crank.json",
                    "--counter-policy", "policy.json",
                    "--identity", "identity.json",
                    "--identity-source", "crank"
                ]));

            Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsDirectAndEnvironmentCredentialIdentityTogether()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                [
                    "upload",
                    "--crank-json", "crank.json",
                    "--counter-policy", "policy.json",
                    "--identity-source", "crank",
                    "--storage-account", "account",
                    "--container", "results",
                    "--queue", "resultsqueue",
                    "--tenant-id", "tenant",
                    "--tenant-id-environment-variable", "TENANT_ID"
                ]));

            Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ParsesBackfillWindowPagingDryRunAndSqlTokenAuthentication()
        {
            var options = ExporterCommandLine.Parse(
            [
                "backfill",
                "--sql-connection-string-environment-variable", "TREND_SQL",
                "--sql-table", "dbo.TrendBenchmarks",
                "--sql-authentication", "token",
                "--sql-access-token-environment-variable", "TREND_SQL_TOKEN",
                "--start-utc", "2026-05-21T00:00:00Z",
                "--end-utc", "2026-08-19T23:59:59Z",
                "--batch-size", "25",
                "--maximum-rows", "250",
                "--dry-run",
                "--benchmarks-commit", "benchmarks-hash",
                "--crank-version", "crank-version",
                "--azdo-project", "internal",
                "--azdo-pipeline", "aspnet-benchmarks",
                "--azdo-build-url-template",
                "https://dev.azure.com/example/internal/_build/results?buildId={buildId}"
            ]);

            var backfill = Assert.IsType<BackfillOptions>(options.Backfill);
            Assert.Equal(ExportMode.Backfill, options.Mode);
            Assert.True(backfill.DryRun);
            Assert.False(backfill.Publish);
            Assert.Equal("dbo.TrendBenchmarks", backfill.Table);
            Assert.Equal(25, backfill.BatchSize);
            Assert.Equal(250, backfill.MaximumRows);
            Assert.Equal(
                SqlAuthenticationMode.AccessToken,
                backfill.SqlAuthentication.Mode);
            Assert.Equal(
                "TREND_SQL_TOKEN",
                backfill.SqlAuthentication.AccessTokenEnvironmentVariable);
            Assert.Equal(
                DateTimeOffset.Parse("2026-05-21T00:00:00Z"),
                backfill.StartUtc);
        }

        [Fact]
        public void RejectsUnsafeBackfillSqlTableBeforeConnecting()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                [
                    "backfill",
                    "--sql-connection-string", "Server=example;Database=db",
                    "--sql-table", "TrendBenchmarks;DROP TABLE x",
                    "--dry-run",
                    "--benchmarks-commit", "benchmarks-hash",
                    "--crank-version", "crank-version",
                    "--azdo-project", "internal",
                    "--azdo-pipeline", "aspnet-benchmarks",
                    "--azdo-build-url-template",
                    "https://dev.azure.com/example/internal/_build/results?buildId={buildId}"
                ]));

            Assert.Contains(
                "SQL table identifier",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Server=example",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void ParsesRepositoryStandardSqlCertificateEnvironmentOptions()
        {
            var options = ExporterCommandLine.Parse(
            [
                "backfill",
                "--sql-connection-string-environment-variable", "SQL_CONNECTION_STRING",
                "--sql-authentication", "certificate",
                "--sql-tenant-id-environment-variable", "SQL_SERVER_TENANTID",
                "--sql-client-id-environment-variable", "SQL_SERVER_CLIENTID",
                "--sql-certificate-path-environment-variable", "SQL_SERVER_CERT_PATH",
                "--dry-run",
                "--benchmarks-commit", "benchmarks-hash",
                "--crank-version", "crank-version",
                "--azdo-project", "internal",
                "--azdo-pipeline", "aspnet-benchmarks",
                "--azdo-build-url-template",
                "https://dev.azure.com/example/internal/_build/results?buildId={buildId}"
            ]);

            var authentication = options.Backfill!.SqlAuthentication;
            Assert.Equal(SqlAuthenticationMode.Certificate, authentication.Mode);
            Assert.Equal(
                "SQL_SERVER_TENANTID",
                authentication.AzureCredential.TenantIdEnvironmentVariable);
            Assert.Equal(
                "SQL_SERVER_CLIENTID",
                authentication.AzureCredential.ClientIdEnvironmentVariable);
            Assert.Equal(
                "SQL_SERVER_CERT_PATH",
                authentication.AzureCredential.CertificatePathEnvironmentVariable);
        }

        [Fact]
        public void BackfillDefaultsToDryRunEvenWhenStorageOptionsArePresent()
        {
            var options = ExporterCommandLine.Parse(
            [
                "backfill",
                "--sql-connection-string-environment-variable", "TREND_SQL",
                "--benchmarks-commit", "benchmarks-hash",
                "--crank-version", "crank-version",
                "--azdo-project", "internal",
                "--azdo-pipeline", "aspnet-benchmarks",
                "--azdo-build-url-template",
                "https://dev.azure.com/example/internal/_build/results?buildId={buildId}",
                "--storage-account", "account",
                "--container", "results",
                "--queue", "resultsqueue"
            ]);

            var backfill = Assert.IsType<BackfillOptions>(options.Backfill);
            Assert.True(backfill.DryRun);
            Assert.False(backfill.Publish);
            Assert.Null(backfill.PublicationConfirmation);
            Assert.Equal("account", backfill.StorageAccount);
        }

        [Fact]
        public void LiveBackfillRequiresPublishAndExactConfirmation()
        {
            string[] baseArguments =
            [
                "backfill",
                "--sql-connection-string-environment-variable", "TREND_SQL",
                "--benchmarks-commit", "benchmarks-hash",
                "--crank-version", "crank-version",
                "--azdo-project", "internal",
                "--azdo-pipeline", "aspnet-benchmarks",
                "--azdo-build-url-template",
                "https://dev.azure.com/example/internal/_build/results?buildId={buildId}",
                "--storage-account", "account",
                "--container", "results",
                "--queue", "resultsqueue",
                "--publish"
            ];

            var missing = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(baseArguments));
            Assert.Contains(
                "--confirm-live-publication",
                missing.Message,
                StringComparison.Ordinal);

            var wrong = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                    baseArguments
                        .Concat(
                        [
                            "--confirm-live-publication",
                            "yes"
                        ])
                        .ToArray()));
            Assert.Contains(
                BackfillPublicationSafety.Confirmation,
                wrong.Message,
                StringComparison.Ordinal);

            var confirmationWithoutPublish =
                Assert.Throws<ArgumentException>(() =>
                    ExporterCommandLine.Parse(
                        baseArguments
                            .Where(argument => argument != "--publish")
                            .Concat(
                            [
                                "--confirm-live-publication",
                                BackfillPublicationSafety.Confirmation
                            ])
                            .ToArray()));
            Assert.Contains(
                "only with the explicit --publish",
                confirmationWithoutPublish.Message,
                StringComparison.Ordinal);

            var options = ExporterCommandLine.Parse(
                baseArguments
                    .Concat(
                    [
                        "--confirm-live-publication",
                        BackfillPublicationSafety.Confirmation
                    ])
                    .ToArray());
            Assert.True(options.Backfill!.Publish);
            Assert.False(options.Backfill.DryRun);
        }

        [Fact]
        public void StorageAuthenticationDefaultsToDefaultAzureCredential()
        {
            var options = ExporterCommandLine.Parse(UploadArguments());

            Assert.Equal(
                StorageAuthenticationMode.Default,
                options.Authentication.Mode);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ParsesExplicitManagedIdentityStorageAuthentication(
            bool includeClientId)
        {
            var arguments = UploadArguments()
                .Concat(
                [
                    "--storage-authentication",
                    "managed-identity"
                ])
                .ToList();
            if (includeClientId)
            {
                arguments.Add("--managed-identity-client-id");
                arguments.Add("user-assigned-client");
            }

            var options = ExporterCommandLine.Parse(arguments.ToArray());

            Assert.Equal(
                StorageAuthenticationMode.ManagedIdentity,
                options.Authentication.Mode);
            Assert.Equal(
                includeClientId ? "user-assigned-client" : null,
                options.Authentication.ManagedIdentityClientId);
        }

        [Fact]
        public void RejectsUnknownStorageAuthenticationMode()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                    UploadArguments()
                        .Concat(
                        [
                            "--storage-authentication",
                            "ambient"
                        ])
                        .ToArray()));

            Assert.Contains(
                "Expected default, managed-identity, or certificate",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, true)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, true, false)]
        public void RejectsEveryPartialStorageCertificateConfiguration(
            bool includeTenant,
            bool includeClient,
            bool includeCertificate,
            bool includePassword)
        {
            const string Tenant = "tenant-sensitive-value";
            const string Client = "client-sensitive-value";
            const string Certificate = "certificate-sensitive-path.pfx";
            var arguments = UploadArguments()
                .Concat(
                [
                    "--storage-authentication",
                    "certificate"
                ])
                .ToList();
            AddCertificateOptions(
                arguments,
                prefix: string.Empty,
                includeTenant,
                includeClient,
                includeCertificate,
                includePassword,
                Tenant,
                Client,
                Certificate);

            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(arguments.ToArray()));

            Assert.Contains(
                "Storage certificate authentication requires",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Tenant, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Client, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Certificate,
                exception.Message,
                StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(
            "default",
            "--managed-identity-client-id",
            "managed-sensitive-value")]
        [InlineData("default", "--tenant-id", "tenant-sensitive-value")]
        [InlineData(
            "managed-identity",
            "--tenant-id",
            "tenant-sensitive-value")]
        [InlineData(
            "certificate",
            "--managed-identity-client-id",
            "managed-sensitive-value")]
        public void RejectsStorageOptionsForAnotherAuthenticationMode(
            string mode,
            string option,
            string sensitiveValue)
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                    UploadArguments()
                        .Concat(
                        [
                            "--storage-authentication",
                            mode,
                            option,
                            sensitiveValue
                        ])
                        .ToArray()));

            Assert.DoesNotContain(
                sensitiveValue,
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void DefaultStorageModeRejectsPartialCertificateTriplet()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                    UploadArguments()
                        .Concat(
                        [
                            "--tenant-id",
                            "tenant-sensitive-value",
                            "--client-id",
                            "client-sensitive-value",
                            "--certificate-password-environment-variable",
                            "CERTIFICATE_PASSWORD"
                        ])
                        .ToArray()));

            Assert.Contains(
                "Storage default authentication",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "tenant-sensitive-value",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "client-sensitive-value",
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void DryRunDoesNotReadStorageCertificateEnvironmentVariables()
        {
            var suffix = Guid.NewGuid().ToString("N");

            var options = ExporterCommandLine.Parse(
                BackfillArguments()
                    .Concat(
                    [
                        "--storage-authentication",
                        "certificate",
                        "--tenant-id-environment-variable",
                        $"MISSING_TENANT_{suffix}",
                        "--client-id-environment-variable",
                        $"MISSING_CLIENT_{suffix}",
                        "--certificate-base64-environment-variable",
                        $"MISSING_CERTIFICATE_{suffix}",
                        "--certificate-password-environment-variable",
                        $"MISSING_PASSWORD_{suffix}"
                    ])
                    .ToArray());

            Assert.True(options.Backfill!.DryRun);
            Assert.Equal(
                StorageAuthenticationMode.Certificate,
                options.Backfill.Authentication.Mode);
        }

        [Theory]
        [InlineData(
            "default",
            "default",
            StorageAuthenticationMode.Default)]
        [InlineData(
            "managed-identity",
            "managed-identity",
            StorageAuthenticationMode.ManagedIdentity)]
        public void ParsesExplicitSqlAzureAuthenticationModes(
            string value,
            string expectedSqlModeValue,
            StorageAuthenticationMode expectedStorageMode)
        {
            var expectedSqlMode = expectedSqlModeValue switch
            {
                "default" =>
                    SqlAuthenticationMode.DefaultAzureCredential,
                "managed-identity" =>
                    SqlAuthenticationMode.ManagedIdentity,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(expectedSqlModeValue),
                    expectedSqlModeValue,
                    null)
            };
            var options = ExporterCommandLine.Parse(
                BackfillArguments()
                    .Concat(
                    [
                        "--sql-authentication",
                        value
                    ])
                    .ToArray());

            Assert.Equal(
                expectedSqlMode,
                options.Backfill!.SqlAuthentication.Mode);
            Assert.Equal(
                expectedStorageMode,
                options.Backfill.SqlAuthentication.AzureCredential.Mode);
        }

        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, true)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, true, false)]
        public void RejectsEveryPartialSqlCertificateConfiguration(
            bool includeTenant,
            bool includeClient,
            bool includeCertificate,
            bool includePassword)
        {
            const string Tenant = "sql-tenant-sensitive-value";
            const string Client = "sql-client-sensitive-value";
            const string Certificate = "sql-certificate-sensitive-path.pfx";
            var arguments = BackfillArguments()
                .Concat(
                [
                    "--sql-authentication",
                    "certificate"
                ])
                .ToList();
            AddCertificateOptions(
                arguments,
                prefix: "sql-",
                includeTenant,
                includeClient,
                includeCertificate,
                includePassword,
                Tenant,
                Client,
                Certificate);

            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(arguments.ToArray()));

            Assert.Contains(
                "SQL certificate authentication requires",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Tenant, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Client, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Certificate,
                exception.Message,
                StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(
            "default",
            "--sql-managed-identity-client-id",
            "managed-sensitive-value")]
        [InlineData(
            "managed-identity",
            "--sql-tenant-id",
            "tenant-sensitive-value")]
        [InlineData(
            "certificate",
            "--sql-managed-identity-client-id",
            "managed-sensitive-value")]
        public void RejectsSqlOptionsForAnotherAuthenticationMode(
            string mode,
            string option,
            string sensitiveValue)
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                    BackfillArguments()
                        .Concat(
                        [
                            "--sql-authentication",
                            mode,
                            option,
                            sensitiveValue
                        ])
                        .ToArray()));

            Assert.DoesNotContain(
                sensitiveValue,
                exception.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void DefaultSqlModeRejectsPartialCertificateTriplet()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                ExporterCommandLine.Parse(
                    BackfillArguments()
                        .Concat(
                        [
                            "--sql-authentication",
                            "default",
                            "--sql-tenant-id",
                            "tenant-sensitive-value",
                            "--sql-client-id",
                            "client-sensitive-value",
                            "--sql-certificate-password-environment-variable",
                            "SQL_CERTIFICATE_PASSWORD"
                        ])
                        .ToArray()));

            Assert.Contains(
                "SQL default authentication",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "tenant-sensitive-value",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "client-sensitive-value",
                exception.Message,
                StringComparison.Ordinal);
        }

        private static string[] UploadArguments()
        {
            return
            [
                "upload",
                "--crank-json", "crank.json",
                "--counter-policy", "policy.json",
                "--identity-source", "crank",
                "--storage-account", "account",
                "--container", "results",
                "--queue", "resultsqueue"
            ];
        }

        private static string[] BackfillArguments()
        {
            return
            [
                "backfill",
                "--sql-connection-string-environment-variable", "TREND_SQL",
                "--benchmarks-commit", "benchmarks-hash",
                "--crank-version", "crank-version",
                "--azdo-project", "internal",
                "--azdo-pipeline", "aspnet-benchmarks",
                "--azdo-build-url-template",
                "https://dev.azure.com/example/internal/_build/results?buildId={buildId}"
            ];
        }

        private static void AddCertificateOptions(
            ICollection<string> arguments,
            string prefix,
            bool includeTenant,
            bool includeClient,
            bool includeCertificate,
            bool includePassword,
            string tenant,
            string client,
            string certificate)
        {
            if (includeTenant)
            {
                arguments.Add($"--{prefix}tenant-id");
                arguments.Add(tenant);
            }

            if (includeClient)
            {
                arguments.Add($"--{prefix}client-id");
                arguments.Add(client);
            }

            if (includeCertificate)
            {
                arguments.Add($"--{prefix}certificate-path");
                arguments.Add(certificate);
            }

            if (includePassword)
            {
                arguments.Add(
                    $"--{prefix}certificate-password-environment-variable");
                arguments.Add($"{prefix}CERTIFICATE_PASSWORD");
            }
        }
    }
}
