// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Crank.PerfLabExporter.Contracts;
using Crank.PerfLabExporter.Contracts.Crank;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class LegacyDocumentException : Exception
    {
        public LegacyDocumentException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    internal static class LegacyJobResultsAdapter
    {
        public static CrankExecutionResult Adapt(LegacyTrendRow row)
        {
            try
            {
                var jobResults = JsonSerializer.Deserialize<CrankJobResults>(
                    row.Document,
                    ContractJson.CreateSerializerOptions());
                if (jobResults is null)
                {
                    throw new LegacyDocumentException(
                        $"Legacy SQL row {row.Id} contains a null JobResults document.");
                }

                return new CrankExecutionResult
                {
                    ReturnCode = 0,
                    CrankVersion = GetProperty(jobResults, "crankVersion"),
                    JobResults = jobResults
                };
            }
            catch (JsonException exception)
            {
                throw new LegacyDocumentException(
                    $"Legacy SQL row {row.Id} does not contain valid Crank JobResults JSON: {exception.Message}",
                    exception);
            }
        }

        private static string? GetProperty(CrankJobResults jobResults, string name)
        {
            var property = jobResults.Properties.FirstOrDefault(pair =>
                pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(property.Key)
                ? null
                : property.Value;
        }
    }
}
