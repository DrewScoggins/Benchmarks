// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.RegularExpressions;
using Crank.PerfLabExporter.Contracts.Crank;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class LegacyMappingResolutionException : Exception
    {
        public LegacyMappingResolutionException(string message)
            : base(message)
        {
        }
    }

    internal sealed record LegacyTrendMappingMatch(
        LegacyLaneRule Lane,
        LegacyScenarioRule Scenario);

    internal static class LegacyTrendMappingMatcher
    {
        private static readonly Regex ProfilePattern = new(
            "(?<![A-Za-z0-9-])([A-Za-z0-9][A-Za-z0-9-]*(?:-app|-load|-db))(?![A-Za-z0-9-])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static LegacyTrendMappingMatch Resolve(
            LegacyTrendMapping mapping,
            LegacyTrendRow row,
            CrankExecutionResult execution)
        {
            var metadata = LegacyMetadata.Create(row, execution);
            var lanes = mapping.LaneRules
                .Where(rule => Matches(rule.MatchAny, metadata))
                .ToList();
            var scenarios = mapping.ScenarioRules
                .Where(rule =>
                    Matches(rule.MatchAny, metadata) ||
                    rule.DescriptionMatches.Any(description =>
                        MatchesDescription(
                            row.Description,
                            description.Description)))
                .ToList();

            var lane = ResolveSingle(lanes, row.Id, "lane");
            var scenario = ResolveSingle(scenarios, row.Id, "scenario");
            return new LegacyTrendMappingMatch(lane, scenario);
        }

        private static T ResolveSingle<T>(
            IReadOnlyList<T> matches,
            long rowId,
            string ruleType)
            where T : class
        {
            if (matches.Count == 1)
            {
                return matches[0];
            }

            var ids = matches.Select(match => match switch
            {
                LegacyLaneRule lane => lane.Id,
                LegacyScenarioRule scenario => scenario.Id,
                _ => "unknown"
            });
            var details = matches.Count == 0
                ? "no rules matched"
                : $"multiple rules matched in configured order: {string.Join(", ", ids)}";
            throw new LegacyMappingResolutionException(
                $"Legacy SQL row {rowId} has unresolved {ruleType} identity: {details}.");
        }

        private static bool Matches(
            IReadOnlyCollection<LegacyMatchCondition> conditions,
            LegacyMetadata metadata)
        {
            return conditions.Any(condition =>
                GetCandidates(condition, metadata).Any(candidate =>
                    Compare(candidate, condition)));
        }

        private static IEnumerable<string> GetCandidates(
            LegacyMatchCondition condition,
            LegacyMetadata metadata)
        {
            return condition.Source.Trim().ToLowerInvariant() switch
            {
                "description" => [metadata.Row.Description],
                "scenario" => [metadata.Row.Scenario],
                "session" => [metadata.Row.Session],
                "profile" => metadata.Profiles,
                "property" => GetKeyedValues(metadata.Properties, condition.Key!),
                "environment" => GetKeyedValues(metadata.Environment, condition.Key!),
                _ => []
            };
        }

        private static IEnumerable<string> GetKeyedValues(
            IReadOnlyDictionary<string, List<string>> values,
            string key)
        {
            foreach (var pair in values)
            {
                if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.EndsWith("." + key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var value in pair.Value)
                    {
                        yield return value;
                    }
                }
            }
        }

        private static bool Compare(
            string candidate,
            LegacyMatchCondition condition)
        {
            if (!string.IsNullOrWhiteSpace(condition.EqualTo))
            {
                return string.Equals(
                    candidate,
                    condition.EqualTo,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(condition.Contains))
            {
                return candidate.Contains(
                    condition.Contains,
                    StringComparison.OrdinalIgnoreCase);
            }

            return Regex.IsMatch(
                candidate,
                condition.Regex!,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }

        internal static bool MatchesDescription(
            string rowDescription,
            string displayName)
        {
            return Regex.IsMatch(
                rowDescription,
                $"^{Regex.Escape(displayName)}\\s+(?:\\d+\\s*-\\s*)?Trends(?:\\s+Database)?\\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }

        private sealed class LegacyMetadata
        {
            private LegacyMetadata(LegacyTrendRow row)
            {
                Row = row;
            }

            public LegacyTrendRow Row { get; }

            public HashSet<string> Profiles { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, List<string>> Properties { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, List<string>> Environment { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public static LegacyMetadata Create(
                LegacyTrendRow row,
                CrankExecutionResult execution)
            {
                var metadata = new LegacyMetadata(row);
                foreach (var property in execution.JobResults.Properties)
                {
                    Add(metadata.Properties, property.Key, property.Value);
                    metadata.AddProfiles(property.Value);
                }

                foreach (var job in execution.JobResults.Jobs)
                {
                    AddElements(metadata, job.Key, job.Value.Environment);
                    AddElements(metadata, job.Key, job.Value.Variables);
                    foreach (var benchmark in job.Value.Benchmarks)
                    {
                        metadata.AddElement(benchmark, job.Key + ".benchmark");
                    }
                }

                foreach (var benchmark in execution.Benchmarks)
                {
                    metadata.AddElement(benchmark, "benchmark");
                }

                metadata.AddProfiles(row.Description);
                return metadata;
            }

            private static void AddElements(
                LegacyMetadata metadata,
                string job,
                IReadOnlyDictionary<string, JsonElement> elements)
            {
                foreach (var element in elements)
                {
                    foreach (var value in EnumerateScalarStrings(element.Value))
                    {
                        Add(metadata.Environment, element.Key, value);
                        Add(metadata.Environment, $"{job}.{element.Key}", value);
                        metadata.AddProfiles(value);
                    }
                }
            }

            private void AddElement(JsonElement element, string key)
            {
                foreach (var value in EnumerateScalarStrings(element))
                {
                    Add(Environment, key, value);
                    AddProfiles(value);
                }
            }

            private void AddProfiles(string value)
            {
                foreach (Match match in ProfilePattern.Matches(value))
                {
                    Profiles.Add(match.Groups[1].Value);
                }
            }

            private static IEnumerable<string> EnumerateScalarStrings(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        yield return element.GetString() ?? string.Empty;
                        break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        yield return element.GetRawText();
                        break;
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            foreach (var value in EnumerateScalarStrings(property.Value))
                            {
                                yield return value;
                            }
                        }

                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray())
                        {
                            foreach (var value in EnumerateScalarStrings(item))
                            {
                                yield return value;
                            }
                        }

                        break;
                }
            }

            private static void Add(
                IDictionary<string, List<string>> destination,
                string key,
                string value)
            {
                if (!destination.TryGetValue(key, out var values))
                {
                    values = [];
                    destination[key] = values;
                }

                values.Add(value);
            }
        }
    }
}
