// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Crank.PerfLabExporter.Backfill;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Publishing;

namespace Crank.PerfLabExporter.Tests
{
    public class AzureCredentialFactoryTests
    {
        [Fact]
        public void DefaultModeCreatesDefaultAzureCredential()
        {
            var credential = AzureCredentialFactory.Create(
                new StorageAuthenticationOptions
                {
                    Mode = StorageAuthenticationMode.Default
                });

            Assert.IsType<DefaultAzureCredential>(credential);
        }

        [Fact]
        public void ManagedIdentityModeCreatesManagedIdentityCredential()
        {
            var credential = AzureCredentialFactory.Create(
                new StorageAuthenticationOptions
                {
                    Mode = StorageAuthenticationMode.ManagedIdentity
                });

            Assert.IsType<ManagedIdentityCredential>(credential);
        }

        [Fact]
        public void ManagedIdentityModeSelectsSystemAssignedIdentity()
        {
            var activator = new RecordingCredentialActivator();

            var credential = AzureCredentialFactory.Create(
                StorageAuthenticationMode.ManagedIdentity,
                new StorageAuthenticationOptions(),
                activator);

            Assert.Same(activator.Credential, credential);
            Assert.Null(activator.ManagedIdentityClientId);
            Assert.Equal(1, activator.ManagedIdentityCalls);
            Assert.Equal(0, activator.DefaultCalls);
        }

        [Fact]
        public void ManagedIdentityModeSelectsDirectUserAssignedIdentity()
        {
            var activator = new RecordingCredentialActivator();

            AzureCredentialFactory.Create(
                StorageAuthenticationMode.ManagedIdentity,
                new StorageAuthenticationOptions
                {
                    ManagedIdentityClientId = "user-assigned-client"
                },
                activator);

            Assert.Equal(
                "user-assigned-client",
                activator.ManagedIdentityClientId);
            Assert.Equal(0, activator.DefaultCalls);
        }

        [Fact]
        public void ManagedIdentityModeSelectsEnvironmentUserAssignedIdentity()
        {
            var environmentVariable = CreateEnvironmentVariableName();
            using var environment = new EnvironmentVariableScope(
                environmentVariable,
                "environment-user-assigned-client");
            var activator = new RecordingCredentialActivator();

            AzureCredentialFactory.Create(
                StorageAuthenticationMode.ManagedIdentity,
                new StorageAuthenticationOptions
                {
                    ManagedIdentityClientIdEnvironmentVariable =
                        environmentVariable
                },
                activator);

            Assert.Equal(
                "environment-user-assigned-client",
                activator.ManagedIdentityClientId);
            Assert.Equal(0, activator.DefaultCalls);
        }

        [Fact]
        public void CertificateEnvironmentConfigurationCreatesCertificateCredential()
        {
            var password = Guid.NewGuid().ToString("N");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=CrankPerfLabExporterTests",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(5));
            var encodedCertificate = Convert.ToBase64String(
                certificate.Export(X509ContentType.Pfx, password));
            var tenantEnvironmentVariable = CreateEnvironmentVariableName();
            var clientEnvironmentVariable = CreateEnvironmentVariableName();
            var certificateEnvironmentVariable =
                CreateEnvironmentVariableName();
            var passwordEnvironmentVariable = CreateEnvironmentVariableName();
            using var tenantEnvironment = new EnvironmentVariableScope(
                tenantEnvironmentVariable,
                "tenant");
            using var clientEnvironment = new EnvironmentVariableScope(
                clientEnvironmentVariable,
                "client");
            using var certificateEnvironment = new EnvironmentVariableScope(
                certificateEnvironmentVariable,
                encodedCertificate);
            using var passwordEnvironment = new EnvironmentVariableScope(
                passwordEnvironmentVariable,
                password);

            var credential = AzureCredentialFactory.Create(
                new StorageAuthenticationOptions
                {
                    Mode = StorageAuthenticationMode.Certificate,
                    TenantIdEnvironmentVariable =
                        tenantEnvironmentVariable,
                    ClientIdEnvironmentVariable =
                        clientEnvironmentVariable,
                    CertificateBase64EnvironmentVariable =
                        certificateEnvironmentVariable,
                    CertificatePasswordEnvironmentVariable =
                        passwordEnvironmentVariable
                });

            Assert.IsType<ClientCertificateCredential>(credential);
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
        public void RejectsEveryPartialCertificateConfigurationWithoutFallback(
            bool includeTenant,
            bool includeClient,
            bool includeCertificate,
            bool includePassword)
        {
            const string Tenant = "tenant-sensitive-value";
            const string Client = "client-sensitive-value";
            const string Certificate = "certificate-sensitive-path.pfx";
            var activator = new RecordingCredentialActivator();

            var exception = Assert.Throws<ArgumentException>(() =>
                AzureCredentialFactory.Create(
                    StorageAuthenticationMode.Certificate,
                    new StorageAuthenticationOptions
                    {
                        TenantId = includeTenant ? Tenant : null,
                        ClientId = includeClient ? Client : null,
                        CertificatePath =
                            includeCertificate ? Certificate : null,
                        CertificatePasswordEnvironmentVariable =
                            includePassword
                                ? CreateEnvironmentVariableName()
                                : null
                    },
                    activator));

            Assert.Contains(
                "Certificate authentication requires",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Tenant, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Client, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Certificate,
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, activator.TotalCalls);
        }

        [Fact]
        public void DefaultModeRejectsPartialCertificateOptionsBeforeAmbientFallback()
        {
            var tenantEnvironmentVariable = CreateEnvironmentVariableName();
            var clientEnvironmentVariable = CreateEnvironmentVariableName();
            var passwordEnvironmentVariable = CreateEnvironmentVariableName();
            var activator = new RecordingCredentialActivator();

            var exception = Assert.Throws<ArgumentException>(() =>
                AzureCredentialFactory.Create(
                    StorageAuthenticationMode.Default,
                    new StorageAuthenticationOptions
                    {
                        TenantIdEnvironmentVariable =
                            tenantEnvironmentVariable,
                        ClientIdEnvironmentVariable =
                            clientEnvironmentVariable,
                        CertificatePasswordEnvironmentVariable =
                            passwordEnvironmentVariable
                    },
                    activator));

            Assert.Contains(
                "Default authentication",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "not set",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, activator.DefaultCalls);
            Assert.Equal(0, activator.TotalCalls);
        }

        [Theory]
        [InlineData("managed-identity")]
        [InlineData("tenant")]
        [InlineData("client")]
        [InlineData("certificate-path")]
        [InlineData("certificate-base64")]
        [InlineData("certificate-password")]
        public void ReportsMissingEnvironmentVariablesForSelectedMode(
            string missingValue)
        {
            var environmentVariable = CreateEnvironmentVariableName();
            using var environment = new EnvironmentVariableScope(
                environmentVariable,
                null);
            var activator = new RecordingCredentialActivator();
            var options = missingValue switch
            {
                "managed-identity" => new StorageAuthenticationOptions
                {
                    ManagedIdentityClientIdEnvironmentVariable =
                        environmentVariable
                },
                "tenant" => new StorageAuthenticationOptions
                {
                    TenantIdEnvironmentVariable = environmentVariable,
                    ClientId = "client",
                    CertificatePath = "unused.pfx"
                },
                "client" => new StorageAuthenticationOptions
                {
                    TenantId = "tenant",
                    ClientIdEnvironmentVariable = environmentVariable,
                    CertificatePath = "unused.pfx"
                },
                "certificate-path" => new StorageAuthenticationOptions
                {
                    TenantId = "tenant",
                    ClientId = "client",
                    CertificatePathEnvironmentVariable =
                        environmentVariable
                },
                "certificate-base64" => new StorageAuthenticationOptions
                {
                    TenantId = "tenant",
                    ClientId = "client",
                    CertificateBase64EnvironmentVariable =
                        environmentVariable
                },
                "certificate-password" => new StorageAuthenticationOptions
                {
                    TenantId = "tenant",
                    ClientId = "client",
                    CertificatePath = "unused.pfx",
                    CertificatePasswordEnvironmentVariable =
                        environmentVariable
                },
                _ => throw new ArgumentOutOfRangeException(
                    nameof(missingValue),
                    missingValue,
                    null)
            };
            var mode = missingValue == "managed-identity"
                ? StorageAuthenticationMode.ManagedIdentity
                : StorageAuthenticationMode.Certificate;

            var exception = Assert.Throws<ArgumentException>(() =>
                AzureCredentialFactory.Create(mode, options, activator));

            Assert.Contains(
                environmentVariable,
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "not set",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, activator.TotalCalls);
        }

        [Fact]
        public void RejectsUnusedModeOptionsWithoutReadingTheirEnvironment()
        {
            var environmentVariable = CreateEnvironmentVariableName();
            using var environment = new EnvironmentVariableScope(
                environmentVariable,
                null);
            var activator = new RecordingCredentialActivator();

            var exception = Assert.Throws<ArgumentException>(() =>
                AzureCredentialFactory.Create(
                    StorageAuthenticationMode.ManagedIdentity,
                    new StorageAuthenticationOptions
                    {
                        TenantIdEnvironmentVariable = environmentVariable
                    },
                    activator));

            Assert.Contains(
                "accepts only",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "not set",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, activator.TotalCalls);
        }

        [Theory]
        [InlineData(
            "default",
            StorageAuthenticationMode.Default)]
        [InlineData(
            "managed-identity",
            StorageAuthenticationMode.ManagedIdentity)]
        [InlineData(
            "certificate",
            StorageAuthenticationMode.Certificate)]
        public void SqlFactorySelectsRequestedAzureCredentialMode(
            string sqlModeValue,
            StorageAuthenticationMode expectedStorageMode)
        {
            var sqlMode = sqlModeValue switch
            {
                "default" => SqlAuthenticationMode.DefaultAzureCredential,
                "managed-identity" =>
                    SqlAuthenticationMode.ManagedIdentity,
                "certificate" => SqlAuthenticationMode.Certificate,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(sqlModeValue),
                    sqlModeValue,
                    null)
            };
            StorageAuthenticationMode? selectedMode = null;
            var credential = new StubTokenCredential();

            var provider = SqlAuthenticationFactory.Create(
                new SqlAuthenticationOptions
                {
                    Mode = sqlMode
                },
                (mode, _) =>
                {
                    selectedMode = mode;
                    return credential;
                });

            Assert.IsType<TokenCredentialSqlAccessTokenProvider>(provider);
            Assert.Equal(expectedStorageMode, selectedMode);
        }

        [Fact]
        public void SqlDefaultModeCreatesDefaultAzureCredential()
        {
            var provider = Assert.IsType<TokenCredentialSqlAccessTokenProvider>(
                SqlAuthenticationFactory.Create(
                    new SqlAuthenticationOptions
                    {
                        Mode = SqlAuthenticationMode.DefaultAzureCredential
                    }));

            Assert.IsType<DefaultAzureCredential>(provider.Credential);
        }

        [Fact]
        public void SqlManagedIdentityModeCreatesManagedIdentityCredential()
        {
            var provider = Assert.IsType<TokenCredentialSqlAccessTokenProvider>(
                SqlAuthenticationFactory.Create(
                    new SqlAuthenticationOptions
                    {
                        Mode = SqlAuthenticationMode.ManagedIdentity,
                        AzureCredential = new StorageAuthenticationOptions
                        {
                            ManagedIdentityClientId =
                                "user-assigned-client"
                        }
                    }));

            Assert.IsType<ManagedIdentityCredential>(provider.Credential);
        }

        private static string CreateEnvironmentVariableName()
        {
            return $"CRANK_PERFLAB_TEST_{Guid.NewGuid():N}";
        }

        private sealed class RecordingCredentialActivator :
            IAzureCredentialActivator
        {
            public StubTokenCredential Credential { get; } = new();

            public int DefaultCalls { get; private set; }

            public int ManagedIdentityCalls { get; private set; }

            public int CertificateCalls { get; private set; }

            public int TotalCalls =>
                DefaultCalls + ManagedIdentityCalls + CertificateCalls;

            public string? ManagedIdentityClientId { get; private set; }

            public TokenCredential CreateDefault()
            {
                DefaultCalls++;
                return Credential;
            }

            public TokenCredential CreateManagedIdentity(string? clientId)
            {
                ManagedIdentityCalls++;
                ManagedIdentityClientId = clientId;
                return Credential;
            }

            public TokenCredential CreateCertificate(
                string tenantId,
                string clientId,
                X509Certificate2 certificate)
            {
                CertificateCalls++;
                return Credential;
            }
        }

        private sealed class StubTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(
                TokenRequestContext requestContext,
                CancellationToken cancellationToken)
            {
                throw new InvalidOperationException(
                    "Tests must not request Azure tokens.");
            }

            public override ValueTask<AccessToken> GetTokenAsync(
                TokenRequestContext requestContext,
                CancellationToken cancellationToken)
            {
                throw new InvalidOperationException(
                    "Tests must not request Azure tokens.");
            }
        }

        private sealed class EnvironmentVariableScope : IDisposable
        {
            private readonly string _name;
            private readonly string? _originalValue;

            public EnvironmentVariableScope(string name, string? value)
            {
                _name = name;
                _originalValue = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(_name, _originalValue);
            }
        }
    }
}
