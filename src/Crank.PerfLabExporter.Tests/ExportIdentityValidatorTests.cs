// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Contracts.Identity;
using Crank.PerfLabExporter.Validation;

namespace Crank.PerfLabExporter.Tests
{
    public class ExportIdentityValidatorTests
    {
        [Fact]
        public void AcceptsStableLaneFamilyAndDependencyIdentity()
        {
            var identity = ContractTestData.CreateValidIdentity();

            var errors = ExportIdentityValidator.Validate(identity);

            Assert.Empty(errors);
        }

        [Fact]
        public void RejectsMissingFamilyAndDuplicateDependencies()
        {
            var identity = ContractTestData.CreateValidIdentity();
            identity.Scenario.Family = string.Empty;
            identity.Dependencies.Add(new DependencyMetadata
            {
                Name = identity.Dependencies[0].Name,
                Repository = "https://github.com/dotnet/aspnetcore"
            });

            var errors = ExportIdentityValidator.Validate(identity);

            Assert.Contains(errors, error => error.Path == "$.scenario.family");
            Assert.Contains(errors, error =>
                error.Path == "$.dependencies" &&
                error.Message.Contains("duplicated", StringComparison.Ordinal));
        }
    }
}
