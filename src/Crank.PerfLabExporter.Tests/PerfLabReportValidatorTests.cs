// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Contracts.PerfLab;
using Crank.PerfLabExporter.Validation;

namespace Crank.PerfLabExporter.Tests
{
    public class PerfLabReportValidatorTests
    {
        [Fact]
        public void AcceptsFalseDirectionForNonTopCounter()
        {
            var report = ContractTestData.CreateValidReport();

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Empty(errors);
            Assert.False(report.Tests[0].Counters[1].HigherIsBetter);
            Assert.False(report.Tests[0].Counters[1].TopCounter);
        }

        [Fact]
        public void RequiresExactlyOneDefaultCounter()
        {
            var report = ContractTestData.CreateValidReport();
            report.Tests[0].Counters[0].DefaultCounter = false;

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Contains(errors, error =>
                error.Path == "$.tests[0].counters" &&
                error.Message.Contains("Exactly one default counter", StringComparison.Ordinal));
        }

        [Fact]
        public void RejectsMultipleDefaultCounters()
        {
            var report = ContractTestData.CreateValidReport();
            report.Tests[0].Counters.Add(new PerfLabCounter
            {
                Name = "Mean latency",
                TopCounter = true,
                DefaultCounter = true,
                HigherIsBetter = false,
                MetricName = "ms",
                Results = [1]
            });

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Contains(errors, error =>
                error.Path == "$.tests[0].counters" &&
                error.Message.Contains("found 2", StringComparison.Ordinal));
        }

        [Fact]
        public void RequiresDefaultCounterToAlsoBeTop()
        {
            var report = ContractTestData.CreateValidReport();
            report.Tests[0].Counters[0].TopCounter = false;

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Contains(errors, error => error.Path == "$.tests[0].counters[0].defaultCounter");
        }

        [Fact]
        public void RequiresUniqueCounterNames()
        {
            var report = ContractTestData.CreateValidReport();
            report.Tests[0].Counters[1].Name = report.Tests[0].Counters[0].Name;

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Contains(errors, error =>
                error.Path == "$.tests[0].counters" &&
                error.Message.Contains("duplicated", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(-0.01)]
        [InlineData(0)]
        [InlineData(1.01)]
        public void RejectsThresholdOutsideFractionalRange(double threshold)
        {
            var report = ContractTestData.CreateValidReport();
            report.Tests[0].Counters[0].RegressionThreshold = threshold;

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Contains(errors, error => error.Path == "$.tests[0].counters[0].regressionThreshold");
        }

        [Fact]
        public void RejectsNonFiniteThreshold()
        {
            var report = ContractTestData.CreateValidReport();
            report.Tests[0].Counters[0].RegressionThreshold = double.NaN;

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Contains(errors, error => error.Path == "$.tests[0].counters[0].regressionThreshold");
        }

        [Fact]
        public void RejectsNonFiniteResult()
        {
            var report = ContractTestData.CreateValidReport();
            report.Tests[0].Counters[0].Results = [double.PositiveInfinity];

            var errors = PerfLabReportValidator.Validate(report);

            Assert.Contains(errors, error => error.Path == "$.tests[0].counters[0].results");
        }
    }
}
