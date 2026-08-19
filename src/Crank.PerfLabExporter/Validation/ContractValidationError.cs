// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Crank.PerfLabExporter.Validation
{
    public sealed record ContractValidationError(string Path, string Message);

    public sealed class ContractValidationException : Exception
    {
        public ContractValidationException(IReadOnlyList<ContractValidationError> errors)
            : base(string.Join(Environment.NewLine, errors.Select(error => $"{error.Path}: {error.Message}")))
        {
            Errors = errors;
        }

        public IReadOnlyList<ContractValidationError> Errors { get; }
    }

    internal static class ValidationRules
    {
        public const double MaximumRegressionThreshold = 1;

        public static bool IsValidThreshold(double? threshold)
        {
            return threshold is null ||
                (double.IsFinite(threshold.Value) &&
                 threshold.Value > 0 &&
                 threshold.Value <= MaximumRegressionThreshold);
        }

        public static void ThrowIfInvalid(IReadOnlyList<ContractValidationError> errors)
        {
            if (errors.Count > 0)
            {
                throw new ContractValidationException(errors);
            }
        }
    }
}
