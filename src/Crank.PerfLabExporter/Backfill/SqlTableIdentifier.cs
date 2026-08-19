// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class SqlTableIdentifier
    {
        private static readonly Regex SegmentPattern = new(
            "^[A-Za-z_][A-Za-z0-9_@$#]*$",
            RegexOptions.CultureInvariant);

        private SqlTableIdentifier(IReadOnlyList<string> segments)
        {
            Segments = segments;
            CanonicalName = string.Join(".", segments);
            QuotedName = string.Join(".", segments.Select(segment => $"[{segment}]"));
        }

        public IReadOnlyList<string> Segments { get; }

        public string CanonicalName { get; }

        public string QuotedName { get; }

        public static SqlTableIdentifier Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("The SQL table identifier cannot be empty.", nameof(value));
            }

            var segments = value
                .Split('.', StringSplitOptions.None)
                .Select(segment => segment.Trim())
                .ToList();
            if (segments.Count is < 1 or > 2 ||
                segments.Any(segment => !SegmentPattern.IsMatch(segment)))
            {
                throw new ArgumentException(
                    "The SQL table identifier must be 'table' or 'schema.table' using only letters, digits, '_', '@', '$', and '#'.",
                    nameof(value));
            }

            return new SqlTableIdentifier(segments);
        }
    }
}
