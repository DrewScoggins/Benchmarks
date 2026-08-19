// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Crank.PerfLabExporter.Contracts.Policy
{
    public sealed class CounterPolicy
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("mappings")]
        public List<CounterMapping> Mappings { get; set; } = [];

        [JsonPropertyName("unmappedCounter")]
        public UnmappedCounterPolicy UnmappedCounter { get; set; } = new();
    }

    public sealed class CounterMapping
    {
        [JsonPropertyName("path")]
        public CrankResultPath? Path { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("metricName")]
        public string MetricName { get; set; } = string.Empty;

        [JsonPropertyName("higherIsBetter")]
        public bool? HigherIsBetter { get; set; }

        [JsonPropertyName("topCounter")]
        public bool TopCounter { get; set; }

        [JsonPropertyName("defaultCounter")]
        public bool DefaultCounter { get; set; }

        [JsonPropertyName("regressionThreshold")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? RegressionThreshold { get; set; }

        [JsonPropertyName("normalization")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CounterNormalization? Normalization { get; set; }

        [JsonPropertyName("applicability")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CounterApplicability? Applicability { get; set; }
    }

    public sealed class CounterNormalization
    {
        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1;

        [JsonPropertyName("offset")]
        public double Offset { get; set; }
    }

    public sealed class CounterApplicability
    {
        [JsonPropertyName("includeScenarioFamilies")]
        public List<string> IncludeScenarioFamilies { get; set; } = [];

        [JsonPropertyName("excludeScenarioFamilies")]
        public List<string> ExcludeScenarioFamilies { get; set; } = [];
    }

    public sealed class UnmappedCounterPolicy
    {
        public const string SourcePathNameTemplate = "{path}";

        [JsonPropertyName("nameTemplate")]
        public string NameTemplate { get; set; } = SourcePathNameTemplate;

        [JsonPropertyName("metricName")]
        public string MetricName { get; set; } = "value";

        [JsonPropertyName("higherIsBetter")]
        public bool? HigherIsBetter { get; set; }

        [JsonPropertyName("topCounter")]
        public bool TopCounter { get; set; }

        [JsonPropertyName("defaultCounter")]
        public bool DefaultCounter { get; set; }
    }
}
