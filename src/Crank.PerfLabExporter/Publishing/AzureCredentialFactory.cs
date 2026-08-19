// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace Crank.PerfLabExporter.Publishing
{
    public sealed class StorageAuthenticationOptions
    {
        public string? ManagedIdentityClientId { get; init; }

        public string? ManagedIdentityClientIdEnvironmentVariable { get; init; }

        public string? TenantId { get; init; }

        public string? TenantIdEnvironmentVariable { get; init; }

        public string? ClientId { get; init; }

        public string? ClientIdEnvironmentVariable { get; init; }

        public string? CertificatePath { get; init; }

        public string? CertificatePathEnvironmentVariable { get; init; }

        public string? CertificateBase64EnvironmentVariable { get; init; }

        public string? CertificatePasswordEnvironmentVariable { get; init; }
    }

    public static class AzureCredentialFactory
    {
        public static TokenCredential Create(StorageAuthenticationOptions options)
        {
            var managedIdentityClientId = ResolveDirectOrEnvironment(
                options.ManagedIdentityClientId,
                options.ManagedIdentityClientIdEnvironmentVariable,
                "managed identity client ID");
            var tenantId = ResolveDirectOrEnvironment(
                options.TenantId,
                options.TenantIdEnvironmentVariable,
                "tenant ID");
            var clientId = ResolveDirectOrEnvironment(
                options.ClientId,
                options.ClientIdEnvironmentVariable,
                "client ID");
            var certificatePath = ResolveDirectOrEnvironment(
                options.CertificatePath,
                options.CertificatePathEnvironmentVariable,
                "certificate path");
            var usesCertificate =
                !string.IsNullOrWhiteSpace(certificatePath) ||
                !string.IsNullOrWhiteSpace(options.CertificateBase64EnvironmentVariable);
            if (!usesCertificate)
            {
                return new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = managedIdentityClientId
                });
            }

            if (string.IsNullOrWhiteSpace(tenantId) ||
                string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException(
                    "Certificate authentication requires both tenant ID and client ID.");
            }

            var password = GetOptionalEnvironmentVariable(
                options.CertificatePasswordEnvironmentVariable,
                "certificate password");
            var credentialOptions = new ClientCertificateCredentialOptions
            {
                SendCertificateChain = true
            };

            if (!string.IsNullOrWhiteSpace(certificatePath))
            {
                certificatePath = Path.GetFullPath(certificatePath);
                var extension = Path.GetExtension(certificatePath);
                X509Certificate2 pathCertificate;
                if (extension.Equals(".pem", StringComparison.OrdinalIgnoreCase))
                {
                    pathCertificate = string.IsNullOrEmpty(password)
                        ? X509Certificate2.CreateFromPemFile(certificatePath)
                        : X509Certificate2.CreateFromEncryptedPemFile(certificatePath, password);
                }
                else
                {
                    pathCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                        certificatePath,
                        password,
                        X509KeyStorageFlags.EphemeralKeySet);
                }

                return new ClientCertificateCredential(
                    tenantId,
                    clientId,
                    pathCertificate,
                    credentialOptions);
            }

            var encodedCertificate = GetRequiredEnvironmentVariable(
                options.CertificateBase64EnvironmentVariable!,
                "base64 certificate");
            byte[] certificateBytes;
            try
            {
                certificateBytes = Convert.FromBase64String(encodedCertificate);
            }
            catch (FormatException exception)
            {
                throw new ArgumentException(
                    $"Environment variable '{options.CertificateBase64EnvironmentVariable}' is not valid base64.",
                    exception);
            }

            var base64Certificate = X509CertificateLoader.LoadPkcs12(
                certificateBytes,
                password,
                X509KeyStorageFlags.EphemeralKeySet);
            return new ClientCertificateCredential(
                tenantId,
                clientId,
                base64Certificate,
                credentialOptions);
        }

        private static string? ResolveDirectOrEnvironment(
            string? directValue,
            string? environmentVariable,
            string description)
        {
            if (!string.IsNullOrWhiteSpace(directValue))
            {
                return directValue;
            }

            return string.IsNullOrWhiteSpace(environmentVariable)
                ? null
                : GetRequiredEnvironmentVariable(environmentVariable, description);
        }

        private static string? GetOptionalEnvironmentVariable(string? name, string description)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return GetRequiredEnvironmentVariable(name, description);
        }

        private static string GetRequiredEnvironmentVariable(string name, string description)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"The {description} environment variable '{name}' is not set.");
            }

            return value;
        }
    }
}
