// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed record LegacyTrendRow(
        long Id,
        bool Excluded,
        DateTimeOffset DateTimeUtc,
        string Session,
        string Scenario,
        string Description,
        string Document);

    internal sealed record LegacyTrendCursor(
        DateTimeOffset DateTimeUtc,
        long Id);

    internal sealed record LegacyTrendQuery(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        LegacyTrendCursor? After,
        int BatchSize);

    internal interface ILegacyTrendRepository
    {
        Task<IReadOnlyList<LegacyTrendRow>> ReadBatchAsync(
            LegacyTrendQuery query,
            CancellationToken cancellationToken);
    }
}
