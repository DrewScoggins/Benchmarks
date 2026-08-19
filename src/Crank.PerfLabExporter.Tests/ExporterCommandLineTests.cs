// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.CommandLine;

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
    }
}
