// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Crank.PerfLabExporter.Naming;

namespace Crank.PerfLabExporter.Tests
{
    public class ExportNamingTests
    {
        [Fact]
        public void ProducesDeterministicFileAndBlobNames()
        {
            var report = ContractTestData.CreateValidReport();
            var identity = ContractTestData.CreateValidIdentity();

            var first = ExportNaming.Create(report, identity);
            var second = ExportNaming.Create(report, identity);

            Assert.Equal(first, second);
            Assert.EndsWith(".perflab.json", first.FileName, StringComparison.Ordinal);
            Assert.StartsWith("crank/aspnet-plaintext/", first.BlobName, StringComparison.Ordinal);
            Assert.EndsWith(first.FileName, first.BlobName, StringComparison.Ordinal);
        }

        [Fact]
        public void IncludesScenarioIdentityInDeterministicHash()
        {
            var report = ContractTestData.CreateValidReport();
            var identity = ContractTestData.CreateValidIdentity();
            var first = ExportNaming.Create(report, identity);
            identity.Sql.Session = "another-session";

            var second = ExportNaming.Create(report, identity);

            Assert.NotEqual(first, second);
        }
    }
}
