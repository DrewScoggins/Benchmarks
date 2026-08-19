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
        CrankResultPath? MappingPath,
        double Value);

    public static class CrankScalarEnumerator
    {
        public static IReadOnlyList<CrankScalar> Enumerate(CrankExecutionResult execution)
        {
            if (execution.ReturnCode != 0)
            {
                throw new CrankConversionException(
                    $"Crank returned exit code {execution.ReturnCode}; failed executions are not exportable.");
            }

            var scalars = new List<CrankScalar>();
            var errors = new List<string>();
            foreach (var job in execution.JobResults.Jobs.OrderBy(job => job.Key, StringComparer.Ordinal))
            {
                foreach (var result in job.Value.Results.OrderBy(result => result.Key, StringComparer.Ordinal))
                {
                    var mappingPath = new CrankResultPath(job.Key, result.Key);
                    EnumerateElement(
                        result.Value,
                        mappingPath.ToString(),
                        mappingPath,
                        isRoot: true,
                        scalars,
                        errors);
                }
            }

            if (errors.Count > 0)
            {
                throw new CrankConversionException(
                    "Crank results contain non-finite or out-of-range numeric scalars:" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Select(error => $"  {error}")));
            }

            return scalars;
        }

        private static void EnumerateElement(
            JsonElement element,
            string sourcePath,
            CrankResultPath mappingPath,
            bool isRoot,
            List<CrankScalar> scalars,
            List<string> errors)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!element.TryGetDouble(out var value) || !double.IsFinite(value))
                    {
                        errors.Add($"{sourcePath}: {element.GetRawText()}");
                    }
                    else
                    {
                        scalars.Add(new CrankScalar(sourcePath, isRoot ? mappingPath : null, value));
                    }
                    break;

                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        EnumerateElement(
                            property.Value,
                            $"{sourcePath}['{Escape(property.Name)}']",
                            mappingPath,
                            isRoot: false,
                            scalars,
                            errors);
                    }
                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        EnumerateElement(
                            item,
                            $"{sourcePath}[{index}]",
                            mappingPath,
                            isRoot: false,
                            scalars,
                            errors);
                        index++;
                    }
                    break;

                case JsonValueKind.String:
                    var text = element.GetString();
                    if (text is not null &&
                        (text.Equals("NaN", StringComparison.OrdinalIgnoreCase) ||
                         text.Equals("Infinity", StringComparison.OrdinalIgnoreCase) ||
                         text.Equals("+Infinity", StringComparison.OrdinalIgnoreCase) ||
                         text.Equals("-Infinity", StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"{sourcePath}: {text}");
                    }
                    break;
            }
        }

        private static string Escape(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
        }
    }
}
