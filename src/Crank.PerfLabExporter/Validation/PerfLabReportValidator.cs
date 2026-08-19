// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Contracts.PerfLab;

namespace Crank.PerfLabExporter.Validation
{
    public static class PerfLabReportValidator
    {
        public static IReadOnlyList<ContractValidationError> Validate(PerfLabReport? report)
        {
            var errors = new List<ContractValidationError>();
            if (report is null)
            {
                errors.Add(new("$", "A PerfLab report is required."));
                return errors;
            }

            var duplicateTests = report.Tests
                .Where(test => !string.IsNullOrWhiteSpace(test.Name))
                .GroupBy(test => test.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var duplicate in duplicateTests)
            {
                errors.Add(new("$.tests", $"Test name '{duplicate.Key}' is duplicated."));
            }

            for (var testIndex = 0; testIndex < report.Tests.Count; testIndex++)
            {
                ValidateTest(report.Tests[testIndex], $"$.tests[{testIndex}]", errors);
            }

            return errors;
        }

        public static void ValidateAndThrow(PerfLabReport? report)
        {
            ValidationRules.ThrowIfInvalid(Validate(report));
        }

        private static void ValidateTest(
            PerfLabTest test,
            string path,
            List<ContractValidationError> errors)
        {
            if (string.IsNullOrWhiteSpace(test.Name))
            {
                errors.Add(new($"{path}.name", "A test name is required."));
            }

            var defaultCount = test.Counters.Count(counter => counter.DefaultCounter);
            if (defaultCount != 1)
            {
                errors.Add(new($"{path}.counters", $"Exactly one default counter is required; found {defaultCount}."));
            }

            var duplicateCounters = test.Counters
                .Where(counter => !string.IsNullOrWhiteSpace(counter.Name))
                .GroupBy(counter => counter.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var duplicate in duplicateCounters)
            {
                errors.Add(new($"{path}.counters", $"Counter name '{duplicate.Key}' is duplicated."));
            }

            for (var counterIndex = 0; counterIndex < test.Counters.Count; counterIndex++)
            {
                ValidateCounter(test.Counters[counterIndex], $"{path}.counters[{counterIndex}]", errors);
            }
        }

        private static void ValidateCounter(
            PerfLabCounter counter,
            string path,
            List<ContractValidationError> errors)
        {
            if (string.IsNullOrWhiteSpace(counter.Name))
            {
                errors.Add(new($"{path}.name", "A counter name is required."));
            }

            if (string.IsNullOrWhiteSpace(counter.MetricName))
            {
                errors.Add(new($"{path}.metricName", "A canonical counter unit is required."));
            }

            if (counter.DefaultCounter && !counter.TopCounter)
            {
                errors.Add(new($"{path}.defaultCounter", "The default counter must also be a top counter."));
            }

            if ((counter.TopCounter || counter.DefaultCounter) && counter.HigherIsBetter is null)
            {
                errors.Add(new($"{path}.higherIsBetter", "Top and default counters require a known direction."));
            }

            if (!ValidationRules.IsValidThreshold(counter.RegressionThreshold))
            {
                errors.Add(new(
                    $"{path}.regressionThreshold",
                    $"A regression threshold must be finite, greater than zero, and no greater than {ValidationRules.MaximumRegressionThreshold}."));
            }
        }
    }
}
