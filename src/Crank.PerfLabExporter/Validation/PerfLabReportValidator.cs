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

            AddRequired(report.Build.Repo, "$.build.repo", "The build repository is required.", errors);
            AddRequired(report.Build.Branch, "$.build.branch", "The build branch is required.", errors);
            AddRequired(report.Build.Architecture, "$.build.architecture", "The build architecture is required.", errors);
            AddRequired(report.Build.Locale, "$.build.locale", "The build locale is required.", errors);
            AddRequired(report.Build.GitHash, "$.build.gitHash", "The build commit is required.", errors);
            AddRequired(report.Build.BuildName, "$.build.buildName", "The build name is required.", errors);
            if (report.Build.TimeStamp == default)
            {
                errors.Add(new("$.build.timeStamp", "The build timestamp is required."));
            }

            AddRequired(report.Os.Name, "$.os.name", "The OS name is required.", errors);
            AddRequired(report.Os.Architecture, "$.os.architecture", "The OS architecture is required.", errors);
            AddRequired(report.Os.Locale, "$.os.locale", "The OS locale is required.", errors);
            AddRequired(report.Run.Name, "$.run.name", "The scenario-family run name is required.", errors);
            AddRequired(report.Run.Queue, "$.run.queue", "The performance lane queue is required.", errors);

            if (!string.IsNullOrWhiteSpace(report.Run.CorrelationId) &&
                (!Guid.TryParse(report.Run.CorrelationId, out var correlationId) || correlationId == Guid.Empty))
            {
                errors.Add(new("$.run.correlationId", "A correlation ID must be a non-empty Helix GUID."));
            }

            if (report.Tests.Count != 1)
            {
                errors.Add(new("$.tests", $"Exactly one test is required for a Crank scenario; found {report.Tests.Count}."));
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

            if (test.Counters.Count == 0)
            {
                errors.Add(new($"{path}.counters", "At least one counter is required."));
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

            if (!ValidationRules.IsValidThreshold(counter.RegressionThreshold))
            {
                errors.Add(new(
                    $"{path}.regressionThreshold",
                    $"A regression threshold must be finite, greater than zero, and no greater than {ValidationRules.MaximumRegressionThreshold}."));
            }

            if (counter.Results is null || counter.Results.Count == 0)
            {
                errors.Add(new($"{path}.results", "At least one independent scalar result is required."));
            }
            else if (counter.Results.Any(result => !double.IsFinite(result)))
            {
                errors.Add(new($"{path}.results", "Counter results must contain only finite numeric scalars."));
            }
        }

        private static void AddRequired(
            string? value,
            string path,
            string message,
            List<ContractValidationError> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new(path, message));
            }
        }
    }
}
