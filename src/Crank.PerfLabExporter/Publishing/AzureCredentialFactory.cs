// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace Crank.PerfLabExporter.Publishing
{
    public enum StorageAuthenticationMode
    {
        Default,
        ManagedIdentity,
        Certificate
    }

    public sealed class StorageAuthenticationOptions
    {
        public StorageAuthenticationMode Mode { get; init; }

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

    internal interface IAzureCredentialActivator
    {
        TokenCredential CreateDefault();

        TokenCredential CreateManagedIdentity(string? clientId);

        TokenCredential CreateCertificate(
            string tenantId,
            string clientId,
            X509Certificate2 certificate);
    }

    internal sealed class AzureCredentialActivator : IAzureCredentialActivator
    {
        public static AzureCredentialActivator Instance { get; } = new();

        private AzureCredentialActivator()
        {
        }

        public TokenCredential CreateDefault()
        {
            return new DefaultAzureCredential();
        }

        public TokenCredential CreateManagedIdentity(string? clientId)
        {
            var managedIdentityId = string.IsNullOrWhiteSpace(clientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId);
            return new ManagedIdentityCredential(managedIdentityId);
        }

        public TokenCredential CreateCertificate(
            string tenantId,
            string clientId,
            X509Certificate2 certificate)
        {
            return new ClientCertificateCredential(
                tenantId,
                clientId,
                certificate,
                new ClientCertificateCredentialOptions
                {
                    SendCertificateChain = true
                });
        }
    }

    public static class AzureCredentialFactory
    {
        public static TokenCredential Create(StorageAuthenticationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(options.Mode, options, AzureCredentialActivator.Instance);
        }

        internal static TokenCredential Create(
            StorageAuthenticationMode mode,
            StorageAuthenticationOptions options)
        {
            return Create(mode, options, AzureCredentialActivator.Instance);
        }

        internal static TokenCredential Create(
            StorageAuthenticationMode mode,
            StorageAuthenticationOptions options,
            IAzureCredentialActivator activator)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(activator);
            Validate(mode, options);

            return mode switch
            {
                StorageAuthenticationMode.Default =>
                    activator.CreateDefault(),
                StorageAuthenticationMode.ManagedIdentity =>
                    activator.CreateManagedIdentity(
                        ResolveDirectOrEnvironment(
                            options.ManagedIdentityClientId,
                            options.ManagedIdentityClientIdEnvironmentVariable,
                            "managed identity client ID")),
                StorageAuthenticationMode.Certificate =>
                    CreateCertificate(options, activator),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(mode),
                    mode,
                    "Unknown storage authentication mode.")
            };
        }

        private static TokenCredential CreateCertificate(
            StorageAuthenticationOptions options,
            IAzureCredentialActivator activator)
        {
            var tenantId = ResolveDirectOrEnvironment(
                options.TenantId,
                options.TenantIdEnvironmentVariable,
                "tenant ID")!;
            var clientId = ResolveDirectOrEnvironment(
                options.ClientId,
                options.ClientIdEnvironmentVariable,
                "client ID")!;
            var certificatePath = ResolveDirectOrEnvironment(
                options.CertificatePath,
                options.CertificatePathEnvironmentVariable,
                "certificate path");
            var password = GetOptionalEnvironmentVariable(
                options.CertificatePasswordEnvironmentVariable,
                "certificate password");

            X509Certificate2 certificate;
            if (!string.IsNullOrWhiteSpace(certificatePath))
            {
                certificatePath = Path.GetFullPath(certificatePath);
                var extension = Path.GetExtension(certificatePath);
                if (extension.Equals(".pem", StringComparison.OrdinalIgnoreCase))
                {
                    certificate = string.IsNullOrEmpty(password)
                        ? X509Certificate2.CreateFromPemFile(certificatePath)
                        : X509Certificate2.CreateFromEncryptedPemFile(
                            certificatePath,
                            password);
                }
                else
                {
                    certificate = X509CertificateLoader.LoadPkcs12FromFile(
                        certificatePath,
                        password,
                        X509KeyStorageFlags.EphemeralKeySet);
                }
            }
            else
            {
                var encodedCertificate = GetRequiredEnvironmentVariable(
                    options.CertificateBase64EnvironmentVariable!,
                    "base64 certificate");
                byte[] certificateBytes;
                try
                {
                    certificateBytes = Convert.FromBase64String(
                        encodedCertificate);
                }
                catch (FormatException exception)
                {
                    throw new ArgumentException(
                        $"Environment variable '{options.CertificateBase64EnvironmentVariable}' is not valid base64.",
                        exception);
                }

                certificate = X509CertificateLoader.LoadPkcs12(
                    certificateBytes,
                    password,
                    X509KeyStorageFlags.EphemeralKeySet);
            }

            return activator.CreateCertificate(
                tenantId,
                clientId,
                certificate);
        }

        private static void Validate(
            StorageAuthenticationMode mode,
            StorageAuthenticationOptions options)
        {
            ValidateDirectOrEnvironment(
                options.ManagedIdentityClientId,
                options.ManagedIdentityClientIdEnvironmentVariable,
                "managed identity client ID");
            ValidateDirectOrEnvironment(
                options.TenantId,
                options.TenantIdEnvironmentVariable,
                "tenant ID");
            ValidateDirectOrEnvironment(
                options.ClientId,
                options.ClientIdEnvironmentVariable,
                "client ID");
            ValidateDirectOrEnvironment(
                options.CertificatePath,
                options.CertificatePathEnvironmentVariable,
                "certificate path");

            var hasManagedIdentity =
                HasValue(options.ManagedIdentityClientId) ||
                HasValue(options.ManagedIdentityClientIdEnvironmentVariable);
            var hasTenant =
                HasValue(options.TenantId) ||
                HasValue(options.TenantIdEnvironmentVariable);
            var hasClient =
                HasValue(options.ClientId) ||
                HasValue(options.ClientIdEnvironmentVariable);
            var hasCertificatePath =
                HasValue(options.CertificatePath) ||
                HasValue(options.CertificatePathEnvironmentVariable);
            var hasBase64Certificate =
                HasValue(options.CertificateBase64EnvironmentVariable);
            var hasCertificatePassword =
                HasValue(options.CertificatePasswordEnvironmentVariable);
            if (hasCertificatePath && hasBase64Certificate)
            {
                throw new ArgumentException(
                    "Use either a certificate path or a base64 certificate environment variable, not both.");
            }

            var hasCertificateOptions =
                hasTenant ||
                hasClient ||
                hasCertificatePath ||
                hasBase64Certificate ||
                hasCertificatePassword;
            switch (mode)
            {
                case StorageAuthenticationMode.Default:
                    if (hasManagedIdentity || hasCertificateOptions)
                    {
                        throw new ArgumentException(
                            "Default authentication does not accept managed identity or certificate options.");
                    }

                    break;
                case StorageAuthenticationMode.ManagedIdentity:
                    if (hasCertificateOptions)
                    {
                        throw new ArgumentException(
                            "Managed identity authentication accepts only a managed identity client ID.");
                    }

                    break;
                case StorageAuthenticationMode.Certificate:
                    if (hasManagedIdentity)
                    {
                        throw new ArgumentException(
                            "Certificate authentication cannot be combined with a managed identity client ID.");
                    }

                    var missing = new List<string>();
                    if (!hasTenant)
                    {
                        missing.Add("tenant ID");
                    }

                    if (!hasClient)
                    {
                        missing.Add("client ID");
                    }

                    if (!hasCertificatePath && !hasBase64Certificate)
                    {
                        missing.Add("certificate path or base64 certificate environment variable");
                    }

                    if (missing.Count > 0)
                    {
                        throw new ArgumentException(
                            $"Certificate authentication requires {string.Join(", ", missing)}.");
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mode),
                        mode,
                        "Unknown storage authentication mode.");
            }
        }

        private static void ValidateDirectOrEnvironment(
            string? directValue,
            string? environmentVariable,
            string description)
        {
            if (HasValue(directValue) && HasValue(environmentVariable))
            {
                throw new ArgumentException(
                    $"Use either a direct {description} or its environment variable, not both.");
            }
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

        private static string? GetOptionalEnvironmentVariable(
            string? name,
            string description)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return GetRequiredEnvironmentVariable(name, description);
        }

        private static string GetRequiredEnvironmentVariable(
            string name,
            string description)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"The {description} environment variable '{name}' is not set.");
            }

            return value;
        }

        private static bool HasValue(string? value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }
    }
}
