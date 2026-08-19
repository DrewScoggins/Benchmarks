// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Crank.PerfLabExporter.Contracts.Crank;
using Crank.PerfLabExporter.Contracts.Policy;

namespace Crank.PerfLabExporter.Conversion
{
    public sealed record CrankScalar(
        string SourcePath,
        CrankResultPath MappingPath,
        double Value);

    public sealed record CrankScalarEnumeration(
        IReadOnlyList<CrankScalar> Scalars,
        IReadOnlyList<string> Diagnostics);

    public static class CrankScalarEnumerator
    {
        public static CrankScalarEnumeration Enumerate(CrankExecutionResult execution)
        {
            if (execution.ReturnCode != 0)
            {
                throw new CrankConversionException(
                    $"Crank returned exit code {execution.ReturnCode}; failed executions are not exportable.");
            }

            var scalars = new List<CrankScalar>();
            var diagnostics = new List<string>();
            var errors = new List<string>();
            foreach (var job in execution.JobResults.Jobs.OrderBy(job => job.Key, StringComparer.Ordinal))
            {
                foreach (var result in job.Value.Results.OrderBy(result => result.Key, StringComparer.Ordinal))
                {
                    var mappingPath = new CrankResultPath(job.Key, result.Key);
                    var sourcePath = mappingPath.ToString();
                    if (result.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (!result.Value.TryGetDouble(out var value) || !double.IsFinite(value))
                        {
                            errors.Add($"{sourcePath}: {result.Value.GetRawText()}");
                        }
                        else
                        {
                            scalars.Add(new CrankScalar(sourcePath, mappingPath, value));
                        }
                    }
                    else if (IsNonFiniteRepresentation(result.Value))
                    {
                        errors.Add($"{sourcePath}: {result.Value.GetString()}");
                    }
                    else
                    {
                        diagnostics.Add(
                            $"Skipped Crank result {sourcePath}: top-level JSON kind " +
                            $"{result.Value.ValueKind} is not a numeric scalar.");
                    }
                }
            }

            if (errors.Count > 0)
            {
                throw new CrankConversionException(
                    "Crank results contain non-finite or out-of-range numeric scalars:" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Select(error => $"  {error}")));
            }

            return new CrankScalarEnumeration(scalars, diagnostics);
        }

        private static bool IsNonFiniteRepresentation(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = element.GetString();
            return text is not null &&
                (text.Equals("NaN", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("Infinity", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("+Infinity", StringComparison.OrdinalIgnoreCase) ||
                 text.Equals("-Infinity", StringComparison.OrdinalIgnoreCase));
        }
    }
}
