// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Crank.PerfLabExporter.Contracts;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class LegacyTrendMapping
    {
        public int SchemaVersion { get; set; }

        public Dictionary<string, string> DefaultConfigurations { get; set; } = [];

        public List<LegacyLaneRule> LaneRules { get; set; } = [];

        public List<LegacyScenarioRule> ScenarioRules { get; set; } = [];
    }

    internal sealed class LegacyLaneRule
    {
        public string Id { get; set; } = string.Empty;

        public List<LegacyMatchCondition> MatchAny { get; set; } = [];

        public string Name { get; set; } = string.Empty;

        public string Queue { get; set; } = string.Empty;

        public string Os { get; set; } = string.Empty;

        public string Architecture { get; set; } = string.Empty;

        public string Locale { get; set; } = string.Empty;

        public int Cores { get; set; }

        public string Hardware { get; set; } = string.Empty;
    }

    internal sealed class LegacyScenarioRule
    {
        public string Id { get; set; } = string.Empty;

        public List<LegacyMatchCondition> MatchAny { get; set; } = [];

        public List<LegacyScenarioDescriptionMatch> DescriptionMatches { get; set; } = [];

        public string TestName { get; set; } = string.Empty;

        public string Family { get; set; } = string.Empty;

        public List<string> Categories { get; set; } = [];

        public string Topology { get; set; } = string.Empty;
    }

    internal sealed class LegacyScenarioDescriptionMatch
    {
        public string Description { get; set; } = string.Empty;

        public string TestName { get; set; } = string.Empty;
    }

    internal sealed class LegacyMatchCondition
    {
        public string Source { get; set; } = string.Empty;

        public string? Key { get; set; }

        [JsonPropertyName("equals")]
        public string? EqualTo { get; set; }

        public string? Contains { get; set; }

        public string? Regex { get; set; }
    }

    internal sealed record LoadedLegacyTrendMapping(
        LegacyTrendMapping Mapping,
        string Fingerprint,
        string SourceName);

    internal static class LegacyTrendMappingLoader
    {
        private static readonly HashSet<string> SupportedSources = new(
            [
                "description",
                "scenario",
                "session",
                "profile",
                "property",
                "environment"
            ],
            StringComparer.Ordinal);

        public static async Task<LoadedLegacyTrendMapping> LoadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            LegacyTrendMapping? mapping;
            try
            {
                mapping = JsonSerializer.Deserialize<LegacyTrendMapping>(
                    bytes,
                    ContractJson.CreateSerializerOptions());
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    $"Could not read legacy mapping '{path}': {exception.Message}",
                    exception);
            }

            Validate(mapping);
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            return new LoadedLegacyTrendMapping(
                mapping!,
                fingerprint,
                Path.GetFileName(path));
        }

        public static void Validate(LegacyTrendMapping? mapping)
        {
            if (mapping is null)
            {
                throw new ArgumentException("The legacy mapping cannot be null.");
            }

            if (mapping.SchemaVersion != 1)
            {
                throw new ArgumentException(
                    $"Unsupported legacy mapping schema version {mapping.SchemaVersion}; expected 1.");
            }

            foreach (var configuration in new[] { "Framework", "Runtime", "Configuration" })
            {
                if (!mapping.DefaultConfigurations.TryGetValue(configuration, out var value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException(
                        $"Legacy mapping default configuration '{configuration}' is required.");
                }
            }

            ValidateUniqueIds(mapping.LaneRules.Select(rule => rule.Id), "lane");
            ValidateUniqueIds(mapping.ScenarioRules.Select(rule => rule.Id), "scenario");
            foreach (var rule in mapping.LaneRules)
            {
                Require(rule.Id, "lane rule ID");
                Require(rule.Name, $"lane rule '{rule.Id}' name");
                Require(rule.Queue, $"lane rule '{rule.Id}' queue");
                Require(rule.Os, $"lane rule '{rule.Id}' OS");
                Require(rule.Architecture, $"lane rule '{rule.Id}' architecture");
                Require(rule.Locale, $"lane rule '{rule.Id}' locale");
                Require(rule.Hardware, $"lane rule '{rule.Id}' hardware");
                if (rule.Cores <= 0)
                {
                    throw new ArgumentException(
                        $"Lane rule '{rule.Id}' must declare a positive core count.");
                }

                ValidateConditions(rule.Id, rule.MatchAny);
            }

            foreach (var rule in mapping.ScenarioRules)
            {
                Require(rule.Id, "scenario rule ID");
                Require(rule.TestName, $"scenario rule '{rule.Id}' test name");
                Require(rule.Family, $"scenario rule '{rule.Id}' family");
                Require(rule.Topology, $"scenario rule '{rule.Id}' topology");
                if (rule.Categories.Count == 0 ||
                    rule.Categories.Any(string.IsNullOrWhiteSpace))
                {
                    throw new ArgumentException(
                        $"Scenario rule '{rule.Id}' must declare non-empty categories.");
                }

                ValidateConditions(rule.Id, rule.MatchAny);
                foreach (var description in rule.DescriptionMatches)
                {
                    Require(
                        description.Description,
                        $"scenario rule '{rule.Id}' description match");
                    Require(
                        description.TestName,
                        $"scenario rule '{rule.Id}' description test name");
                }

                var duplicateDescriptions = rule.DescriptionMatches
                    .GroupBy(
                        description => description.Description,
                        StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateDescriptions is not null)
                {
                    throw new ArgumentException(
                        $"Scenario rule '{rule.Id}' description '{duplicateDescriptions.Key}' is duplicated.");
                }
            }
        }

        private static void ValidateConditions(
            string ruleId,
            IReadOnlyCollection<LegacyMatchCondition> conditions)
        {
            if (conditions.Count == 0)
            {
                throw new ArgumentException(
                    $"Legacy mapping rule '{ruleId}' must declare at least one match condition.");
            }

            foreach (var condition in conditions)
            {
                var source = condition.Source.Trim().ToLowerInvariant();
                if (!SupportedSources.Contains(source))
                {
                    throw new ArgumentException(
                        $"Legacy mapping rule '{ruleId}' uses unsupported source '{condition.Source}'.");
                }

                if (source is "property" or "environment" &&
                    string.IsNullOrWhiteSpace(condition.Key))
                {
                    throw new ArgumentException(
                        $"Legacy mapping rule '{ruleId}' source '{source}' requires a key.");
                }

                var operatorCount =
                    (string.IsNullOrWhiteSpace(condition.EqualTo) ? 0 : 1) +
                    (string.IsNullOrWhiteSpace(condition.Contains) ? 0 : 1) +
                    (string.IsNullOrWhiteSpace(condition.Regex) ? 0 : 1);
                if (operatorCount != 1)
                {
                    throw new ArgumentException(
                        $"Legacy mapping rule '{ruleId}' conditions require exactly one of equals, contains, or regex.");
                }

                if (!string.IsNullOrWhiteSpace(condition.Regex))
                {
                    try
                    {
                        _ = new Regex(
                            condition.Regex,
                            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                            TimeSpan.FromSeconds(1));
                    }
                    catch (ArgumentException exception)
                    {
                        throw new ArgumentException(
                            $"Legacy mapping rule '{ruleId}' contains invalid regex '{condition.Regex}'.",
                            exception);
                    }
                }
            }
        }

        private static void ValidateUniqueIds(
            IEnumerable<string> ids,
            string ruleType)
        {
            var duplicate = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .GroupBy(id => id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new ArgumentException(
                    $"Legacy mapping {ruleType} rule ID '{duplicate.Key}' is duplicated.");
            }
        }

        private static void Require(string value, string description)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"The {description} is required.");
            }
        }
    }
}
