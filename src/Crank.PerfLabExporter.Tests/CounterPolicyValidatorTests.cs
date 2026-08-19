// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Contracts.Policy;
using Crank.PerfLabExporter.Validation;

namespace Crank.PerfLabExporter.Tests
{
    public class CounterPolicyValidatorTests
    {
        [Fact]
        public void AcceptsValidPolicyAndStorageOnlyFallback()
        {
            var policy = ContractTestData.CreateValidPolicy();

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Empty(errors);
            Assert.Equal(UnmappedCounterPolicy.SourcePathNameTemplate, policy.UnmappedCounter.NameTemplate);
            Assert.False(policy.UnmappedCounter.TopCounter);
            Assert.False(policy.UnmappedCounter.DefaultCounter);
            Assert.Null(policy.UnmappedCounter.HigherIsBetter);
        }

        [Fact]
        public void RequiresExactlyOneDefaultMapping()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[0].DefaultCounter = false;

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error =>
                error.Path == "$.mappings" &&
                error.Message.Contains("Exactly one default mapping", StringComparison.Ordinal));
        }

        [Fact]
        public void RequiresDefaultMappingToAlsoBeTop()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[0].TopCounter = false;

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error => error.Path == "$.mappings[0].defaultCounter");
        }

        [Fact]
        public void RequiresKnownDirectionForTopMapping()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[1].HigherIsBetter = null;

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error => error.Path == "$.mappings[1].higherIsBetter");
        }

        [Fact]
        public void RequiresUniqueFullyQualifiedMappings()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[1].Path = policy.Mappings[0].Path;

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error =>
                error.Path == "$.mappings" &&
                error.Message.Contains("Crank path", StringComparison.Ordinal));
        }

        [Fact]
        public void RequiresUniqueCanonicalCounterNames()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[1].Name = policy.Mappings[0].Name;

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error =>
                error.Path == "$.mappings" &&
                error.Message.Contains("Counter name", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(-0.01)]
        [InlineData(0)]
        [InlineData(1.01)]
        public void RejectsInvalidFractionalThreshold(double threshold)
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[0].RegressionThreshold = threshold;

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error => error.Path == "$.mappings[0].regressionThreshold");
        }

        [Fact]
        public void RejectsInvalidNormalization()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[1].Normalization = new CounterNormalization
            {
                Scale = 0,
                Offset = double.PositiveInfinity
            };

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error => error.Path == "$.mappings[1].normalization.scale");
            Assert.Contains(errors, error => error.Path == "$.mappings[1].normalization.offset");
        }

        [Fact]
        public void RequiresUnknownFallbackToRemainNonTop()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.UnmappedCounter.TopCounter = true;
            policy.UnmappedCounter.HigherIsBetter = true;

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(errors, error => error.Path == "$.unmappedCounter");
            Assert.Contains(errors, error => error.Path == "$.unmappedCounter.higherIsBetter");
        }

        [Fact]
        public void RejectsEmptyOrOverlappingFamilyApplicability()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[1].Applicability = new CounterApplicability();

            var emptyErrors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(
                emptyErrors,
                error => error.Path == "$.mappings[1].applicability");

            policy.Mappings[1].Applicability = new CounterApplicability
            {
                IncludeScenarioFamilies =
                [
                    "aspnet-plaintext",
                    "aspnet-plaintext"
                ],
                ExcludeScenarioFamilies =
                [
                    "aspnet-plaintext",
                    " "
                ]
            };

            var overlapErrors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(
                overlapErrors,
                error => error.Path ==
                    "$.mappings[1].applicability.includeScenarioFamilies");
            Assert.Contains(
                overlapErrors,
                error => error.Path ==
                    "$.mappings[1].applicability.excludeScenarioFamilies[1]");
            Assert.Contains(
                overlapErrors,
                error => error.Path == "$.mappings[1].applicability" &&
                    error.Message.Contains(
                        "both included and excluded",
                        StringComparison.Ordinal));
        }

        [Fact]
        public void RequiresDefaultMappingToApplyToEveryFamily()
        {
            var policy = ContractTestData.CreateValidPolicy();
            policy.Mappings[0].Applicability = new CounterApplicability
            {
                ExcludeScenarioFamilies = ["aspnet-request-rejection"]
            };

            var errors = CounterPolicyValidator.Validate(policy);

            Assert.Contains(
                errors,
                error => error.Path == "$.mappings[0].applicability" &&
                    error.Message.Contains(
                        "default mapping",
                        StringComparison.Ordinal));
        }
    }
}
