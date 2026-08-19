// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Contracts.Identity;

namespace Crank.PerfLabExporter.Validation
{
    public static class ExportIdentityValidator
    {
        public static IReadOnlyList<ContractValidationError> Validate(ExportIdentity? identity)
        {
            var errors = new List<ContractValidationError>();
            if (identity is null)
            {
                errors.Add(new("$", "Export identity is required."));
                return errors;
            }

            AddRequired(identity.Build.Repo, "$.build.repo", "The primary build repository is required.", errors);
            AddRequired(identity.Build.Branch, "$.build.branch", "The primary build branch is required.", errors);
            AddRequired(identity.Build.GitHash, "$.build.gitHash", "The primary build commit is required.", errors);
            AddRequired(identity.Build.BuildName, "$.build.buildName", "The primary build name is required.", errors);

            AddRequired(identity.Lane.Name, "$.lane.name", "A stable lane name is required.", errors);
            AddRequired(identity.Lane.Queue, "$.lane.queue", "A PerfLab queue identity is required.", errors);
            AddRequired(identity.Lane.Os.Name, "$.lane.os.name", "The lane OS name is required.", errors);
            AddRequired(identity.Lane.Os.Architecture, "$.lane.os.architecture", "The lane architecture is required.", errors);
            AddRequired(identity.Lane.Os.Locale, "$.lane.os.locale", "The lane locale is required.", errors);

            AddRequired(identity.Scenario.Name, "$.scenario.name", "A scenario name is required.", errors);
            AddRequired(identity.Scenario.Family, "$.scenario.family", "A stable scenario-family identity is required.", errors);

            var duplicateDependencies = identity.Dependencies
                .Where(dependency => !string.IsNullOrWhiteSpace(dependency.Name))
                .GroupBy(dependency => dependency.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1);

            foreach (var duplicate in duplicateDependencies)
            {
                errors.Add(new("$.dependencies", $"Dependency name '{duplicate.Key}' is duplicated."));
            }

            for (var dependencyIndex = 0; dependencyIndex < identity.Dependencies.Count; dependencyIndex++)
            {
                var dependency = identity.Dependencies[dependencyIndex];
                AddRequired(
                    dependency.Name,
                    $"$.dependencies[{dependencyIndex}].name",
                    "A dependency name is required.",
                    errors);
                AddRequired(
                    dependency.Repository,
                    $"$.dependencies[{dependencyIndex}].repository",
                    "A dependency repository is required.",
                    errors);
            }

            return errors;
        }

        public static void ValidateAndThrow(ExportIdentity? identity)
        {
            ValidationRules.ThrowIfInvalid(Validate(identity));
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
