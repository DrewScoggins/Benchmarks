// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Crank.PerfLabExporter.Backfill
{
    internal static class BackfillPublicationSafety
    {
        public const string Confirmation = "PUBLISH_TREND_BACKFILL";

        public static void Validate(bool publish, string? confirmation)
        {
            if (!publish)
            {
                if (!string.IsNullOrWhiteSpace(confirmation))
                {
                    throw new ArgumentException(
                        "--confirm-live-publication is valid only with the explicit --publish opt-in.");
                }

                return;
            }

            if (!string.Equals(
                    confirmation,
                    Confirmation,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Live backfill publication requires both --publish and " +
                    $"--confirm-live-publication {Confirmation}.");
            }
        }
    }
}
