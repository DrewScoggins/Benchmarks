// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Azure.Core;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Publishing;
using Microsoft.Data.SqlClient;

namespace Crank.PerfLabExporter.Backfill
{
    internal interface ISqlAccessTokenProvider
    {
        ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
    }

    internal sealed class TokenCredentialSqlAccessTokenProvider : ISqlAccessTokenProvider
    {
        private static readonly TokenRequestContext RequestContext = new(
            ["https://database.windows.net/.default"]);
        private readonly TokenCredential _credential;

        public TokenCredentialSqlAccessTokenProvider(TokenCredential credential)
        {
            _credential = credential;
        }

        public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            var token = await _credential.GetTokenAsync(RequestContext, cancellationToken);
            return token.Token;
        }
    }

    internal sealed class EnvironmentSqlAccessTokenProvider : ISqlAccessTokenProvider
    {
        private readonly string _environmentVariable;

        public EnvironmentSqlAccessTokenProvider(string environmentVariable)
        {
            _environmentVariable = environmentVariable;
        }

        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = Environment.GetEnvironmentVariable(_environmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"The SQL access-token environment variable '{_environmentVariable}' is not set.");
            }

            return ValueTask.FromResult(value);
        }
    }

    internal static class SqlAuthenticationFactory
    {
        public static ISqlAccessTokenProvider? Create(SqlAuthenticationOptions options)
        {
            return options.Mode switch
            {
                SqlAuthenticationMode.ConnectionString => null,
                SqlAuthenticationMode.AccessToken =>
                    new EnvironmentSqlAccessTokenProvider(
                        options.AccessTokenEnvironmentVariable!),
                SqlAuthenticationMode.DefaultAzureCredential or
                SqlAuthenticationMode.ManagedIdentity or
                SqlAuthenticationMode.Certificate =>
                    new TokenCredentialSqlAccessTokenProvider(
                        AzureCredentialFactory.Create(options.AzureCredential)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.Mode,
                    "Unknown SQL authentication mode.")
            };
        }
    }

    internal static class SqlConnectionStringResolver
    {
        public static string Resolve(
            string directValue,
            string? environmentVariable)
        {
            var value = !string.IsNullOrWhiteSpace(directValue)
                ? directValue
                : string.IsNullOrWhiteSpace(environmentVariable)
                    ? null
                    : Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                var source = string.IsNullOrWhiteSpace(environmentVariable)
                    ? "the command line"
                    : $"environment variable '{environmentVariable}'";
                throw new ArgumentException(
                    $"A non-empty SQL connection string was not available from {source}.");
            }

            try
            {
                _ = new SqlConnectionStringBuilder(value);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "The configured SQL connection string is invalid.",
                    exception);
            }

            return value;
        }

        public static string CreateSourceIdentity(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return $"{builder.DataSource}|{builder.InitialCatalog}";
        }
    }
}
