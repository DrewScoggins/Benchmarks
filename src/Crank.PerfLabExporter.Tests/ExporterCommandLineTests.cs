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
    }
}
