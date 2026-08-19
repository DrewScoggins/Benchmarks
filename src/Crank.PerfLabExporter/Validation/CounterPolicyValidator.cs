// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Contracts.Policy;

namespace Crank.PerfLabExporter.Validation
{
    public static class CounterPolicyValidator
    {
        public static IReadOnlyList<ContractValidationError> Validate(CounterPolicy? policy)
        {
            var errors = new List<ContractValidationError>();
            if (policy is null)
            {
                errors.Add(new("$", "A counter policy is required."));
                return errors;
            }

            if (policy.SchemaVersion <= 0)
            {
                errors.Add(new("$.schemaVersion", "The schema version must be positive."));
            }

            var defaultCount = policy.Mappings.Count(mapping => mapping.DefaultCounter);
            if (defaultCount != 1)
            {
                errors.Add(new("$.mappings", $"Exactly one default mapping is required; found {defaultCount}."));
            }

            AddDuplicateErrors(
                policy.Mappings.Where(mapping => mapping.Path is not null).GroupBy(mapping => mapping.Path!),
                "$.mappings",
                "Crank path",
                errors);

            AddDuplicateErrors(
                policy.Mappings
                    .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Name))
                    .GroupBy(mapping => mapping.Name, StringComparer.Ordinal),
                "$.mappings",
                "Counter name",
                errors);

            for (var mappingIndex = 0; mappingIndex < policy.Mappings.Count; mappingIndex++)
            {
                ValidateMapping(policy.Mappings[mappingIndex], $"$.mappings[{mappingIndex}]", errors);
            }

            ValidateUnmappedCounter(policy.UnmappedCounter, "$.unmappedCounter", errors);
            return errors;
        }

        public static void ValidateAndThrow(CounterPolicy? policy)
        {
            ValidationRules.ThrowIfInvalid(Validate(policy));
        }

        private static void ValidateMapping(
            CounterMapping mapping,
            string path,
            List<ContractValidationError> errors)
        {
            if (mapping.Path is null ||
                string.IsNullOrWhiteSpace(mapping.Path.Job) ||
                string.IsNullOrWhiteSpace(mapping.Path.Result))
            {
                errors.Add(new($"{path}.path", "A fully qualified Crank job/result path is required."));
            }

            if (string.IsNullOrWhiteSpace(mapping.Name))
            {
                errors.Add(new($"{path}.name", "A canonical counter name is required."));
            }

            if (string.IsNullOrWhiteSpace(mapping.MetricName))
            {
                errors.Add(new($"{path}.metricName", "A canonical counter unit is required."));
            }

            if (mapping.DefaultCounter && !mapping.TopCounter)
            {
                errors.Add(new($"{path}.defaultCounter", "The default mapping must also be a top mapping."));
            }

            if ((mapping.TopCounter || mapping.DefaultCounter) && mapping.HigherIsBetter is null)
            {
                errors.Add(new($"{path}.higherIsBetter", "Top and default mappings require a known direction."));
            }

            if (!ValidationRules.IsValidThreshold(mapping.RegressionThreshold))
            {
                errors.Add(new(
                    $"{path}.regressionThreshold",
                    $"A regression threshold must be finite, greater than zero, and no greater than {ValidationRules.MaximumRegressionThreshold}."));
            }

            if (mapping.Normalization is not null)
            {
                if (!double.IsFinite(mapping.Normalization.Scale) || mapping.Normalization.Scale <= 0)
                {
                    errors.Add(new($"{path}.normalization.scale", "The normalization scale must be finite and greater than zero."));
                }

                if (!double.IsFinite(mapping.Normalization.Offset))
                {
                    errors.Add(new($"{path}.normalization.offset", "The normalization offset must be finite."));
                }
            }
        }

        private static void ValidateUnmappedCounter(
            UnmappedCounterPolicy fallback,
            string path,
            List<ContractValidationError> errors)
        {
            if (!string.Equals(
                fallback.NameTemplate,
                UnmappedCounterPolicy.SourcePathNameTemplate,
                StringComparison.Ordinal))
            {
                errors.Add(new($"{path}.nameTemplate", "Unmapped counters must retain their fully qualified source path as their name."));
            }

            if (string.IsNullOrWhiteSpace(fallback.MetricName))
            {
                errors.Add(new($"{path}.metricName", "A fallback unit is required."));
            }

            if (fallback.TopCounter || fallback.DefaultCounter)
            {
                errors.Add(new(path, "Unmapped counters must be non-top and non-default."));
            }

            if (fallback.HigherIsBetter is not null)
            {
                errors.Add(new($"{path}.higherIsBetter", "Unmapped counters must have unknown direction."));
            }
        }

        private static void AddDuplicateErrors<TKey>(
            IEnumerable<IGrouping<TKey, CounterMapping>> groups,
            string path,
            string label,
            List<ContractValidationError> errors)
            where TKey : notnull
        {
            foreach (var duplicate in groups.Where(group => group.Count() > 1))
            {
                errors.Add(new(path, $"{label} '{duplicate.Key}' is duplicated."));
            }
        }
    }
}
