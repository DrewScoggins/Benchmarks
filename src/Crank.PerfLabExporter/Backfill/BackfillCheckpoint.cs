// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using Crank.PerfLabExporter.CommandLine;
using Crank.PerfLabExporter.Contracts;
using Crank.PerfLabExporter.IO;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class BackfillCheckpoint
    {
        public int SchemaVersion { get; set; } = 1;

        public string ConfigurationFingerprint { get; set; } = string.Empty;

        public string MappingFingerprint { get; set; } = string.Empty;

        public DateTimeOffset StartUtc { get; set; }

        public DateTimeOffset EndUtc { get; set; }

        public long? LastCompletedSqlId { get; set; }

        public DateTimeOffset? LastCompletedSqlDateTimeUtc { get; set; }
    }

    internal sealed class BackfillCheckpointStore
    {
        private readonly string _path;

        public BackfillCheckpointStore(string path)
        {
            _path = Path.GetFullPath(path);
        }

        public async Task<BackfillCheckpoint?> LoadAsync(
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                var checkpoint = await JsonSerializer.DeserializeAsync<BackfillCheckpoint>(
                    stream,
                    ContractJson.CreateSerializerOptions(),
                    cancellationToken);
                return checkpoint ?? throw new InvalidDataException(
                    $"Backfill checkpoint '{_path}' contains JSON null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Backfill checkpoint '{_path}' is invalid: {exception.Message}",
                    exception);
            }
        }

        public Task SaveAsync(
            BackfillCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                checkpoint,
                ContractJson.CreateSerializerOptions(writeIndented: true));
            return AtomicFileWriter.WriteAsync(_path, bytes, cancellationToken);
        }
    }

    internal sealed record TrendBackfillExecutionOptions(
        DateTimeOffset? StartUtc,
        DateTimeOffset? EndUtc,
        int BatchSize,
        int? MaximumRows,
        bool DryRun,
        string Table,
        string SqlSourceIdentity,
        string SqlAuthenticationMode,
        string MappingFingerprint,
        string MappingSourceName,
        string PolicyFingerprint,
        string PolicySourceName,
        string OutputDirectory,
        string CheckpointPath,
        string? StorageAccount,
        string? Container,
        string? Queue,
        BackfillIdentityOptions Identity);

    internal static class BackfillConfigurationFingerprint
    {
        public static string Create(
            TrendBackfillExecutionOptions options,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc)
        {
            var payload = new
            {
                schemaVersion = 1,
                startUtc = startUtc.ToUniversalTime(),
                endUtc = endUtc.ToUniversalTime(),
                options.DryRun,
                options.Table,
                options.SqlSourceIdentity,
                options.SqlAuthenticationMode,
                options.MappingFingerprint,
                options.PolicyFingerprint,
                options.StorageAccount,
                options.Container,
                options.Queue,
                identity = new
                {
                    options.Identity.RuntimeBranch,
                    options.Identity.BenchmarksCommit,
                    options.Identity.CrankVersion,
                    options.Identity.AzureDevOpsProject,
                    options.Identity.AzureDevOpsPipeline,
                    options.Identity.AzureDevOpsBuildId,
                    options.Identity.AzureDevOpsBuildNumber,
                    options.Identity.AzureDevOpsBuildUrlTemplate,
                    options.Identity.RuntimeArtifactId
                }
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                ContractJson.CreateSerializerOptions());
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}
