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
            if (identity.Build.TimeStamp is { } timestamp && timestamp == default)
            {
                errors.Add(new("$.build.timeStamp", "When supplied, the primary build timestamp must be non-default."));
            }

            AddRequired(identity.Lane.Name, "$.lane.name", "A stable lane name is required.", errors);
            AddRequired(identity.Lane.Queue, "$.lane.queue", "A PerfLab queue identity is required.", errors);
            AddRequired(identity.Lane.Os.Name, "$.lane.os.name", "The lane OS name is required.", errors);
            AddRequired(identity.Lane.Os.Architecture, "$.lane.os.architecture", "The lane architecture is required.", errors);
            AddRequired(identity.Lane.Os.Locale, "$.lane.os.locale", "The lane locale is required.", errors);

            AddRequired(identity.Scenario.Name, "$.scenario.name", "A scenario name is required.", errors);
            AddRequired(identity.Scenario.Family, "$.scenario.family", "A stable scenario-family identity is required.", errors);
            AddRequired(identity.PerfRepoHash, "$.perfRepoHash", "The pinned Benchmarks commit is required.", errors);
            AddRequired(identity.CrankVersion, "$.crankVersion", "The Crank version is required.", errors);
            AddRequired(identity.AzureDevOps.Project, "$.azureDevOps.project", "The Azure DevOps project is required.", errors);
            AddRequired(identity.AzureDevOps.Pipeline, "$.azureDevOps.pipeline", "The Azure DevOps pipeline is required.", errors);
            AddRequired(identity.AzureDevOps.BuildId, "$.azureDevOps.buildId", "The Azure DevOps build ID is required.", errors);
            AddRequired(identity.AzureDevOps.BuildNumber, "$.azureDevOps.buildNumber", "The Azure DevOps build number is required.", errors);
            AddRequired(identity.AzureDevOps.BuildUrl, "$.azureDevOps.buildUrl", "The Azure DevOps build URL is required.", errors);
            AddRequired(identity.Sql.Session, "$.sql.session", "The Crank SQL/session identity is required.", errors);

            if (!string.IsNullOrWhiteSpace(identity.AzureDevOps.BuildUrl) &&
                (!Uri.TryCreate(identity.AzureDevOps.BuildUrl, UriKind.Absolute, out var buildUri) ||
                 buildUri.Scheme is not ("http" or "https")))
            {
                errors.Add(new("$.azureDevOps.buildUrl", "The Azure DevOps build URL must be an absolute HTTP(S) URL."));
            }

            if (!string.IsNullOrWhiteSpace(identity.HelixCorrelationId) &&
                (!Guid.TryParse(identity.HelixCorrelationId, out var correlationId) || correlationId == Guid.Empty))
            {
                errors.Add(new("$.helixCorrelationId", "A Helix correlation ID must be a non-empty GUID."));
            }

            foreach (var configuration in identity.Lane.Configurations)
            {
                if (string.IsNullOrWhiteSpace(configuration.Key) || string.IsNullOrWhiteSpace(configuration.Value))
                {
                    errors.Add(new("$.lane.configurations", "Configuration names and values must be non-empty."));
                    break;
                }
            }

            var duplicateDependencies = identity.Dependencies
                .Where(dependency => !string.IsNullOrWhiteSpace(dependency.Name))
                .GroupBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase)
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
