// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Crank.PerfLabExporter.Contracts.Policy
{
    [JsonConverter(typeof(CrankResultPathJsonConverter))]
    public sealed record CrankResultPath(string Job, string Result)
    {
        private static readonly Regex DotPathPattern = new(
            @"^jobs\.(?<job>[^.\[\]']+)\.results\['(?<result>(?:\\['\\]|[^'\\])*)'\]$",
            RegexOptions.CultureInvariant);

        private static readonly Regex BracketPathPattern = new(
            @"^jobs\['(?<job>(?:\\['\\]|[^'\\])*)'\]\.results\['(?<result>(?:\\['\\]|[^'\\])*)'\]$",
            RegexOptions.CultureInvariant);

        public static bool TryParse(string? value, out CrankResultPath? path)
        {
            path = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var match = DotPathPattern.Match(value);
            if (!match.Success)
            {
                match = BracketPathPattern.Match(value);
            }

            if (!match.Success)
            {
                return false;
            }

            var job = Unescape(match.Groups["job"].Value);
            var result = Unescape(match.Groups["result"].Value);
            if (string.IsNullOrWhiteSpace(job) || string.IsNullOrWhiteSpace(result))
            {
                return false;
            }

            path = new CrankResultPath(job, result);
            return true;
        }

        public override string ToString()
        {
            var job = IsSimpleJobName(Job) ? $".{Job}" : $"['{Escape(Job)}']";
            return $"jobs{job}.results['{Escape(Result)}']";
        }

        private static bool IsSimpleJobName(string value)
        {
            return value.Length > 0 && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
        }

        private static string Escape(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);
        }

        private static string Unescape(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '\\' && index + 1 < value.Length)
                {
                    index++;
                }

                builder.Append(value[index]);
            }

            return builder.ToString();
        }
    }

    internal sealed class CrankResultPathJsonConverter : JsonConverter<CrankResultPath>
    {
        public override CrankResultPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (!CrankResultPath.TryParse(value, out var path))
            {
                throw new JsonException($"'{value}' is not a fully qualified Crank job/result path.");
            }

            return path!;
        }

        public override void Write(Utf8JsonWriter writer, CrankResultPath value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
