/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

namespace ServiceBackendConfigurationPlugin.Integration.Test
{
    using System.Linq;
    using System.Reflection;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

    /// <summary>
    /// flutter-chemistry spec §12: the legacy eForm chemical flow is removed.
    /// The 02:00 register sync in SearchListJob stays; the 03:00/04:00
    /// entity-list and KemiKontrol report steps and the case handler go.
    /// </summary>
    [TestFixture]
    public class LegacyChemicalFlowRemovedTests
    {
        private static readonly Assembly ServiceAssembly = typeof(SearchListJob).Assembly;

        [Test]
        public void ChemicalCaseCompletedMessageAndHandlerAreGone()
        {
            var typeNames = ServiceAssembly.GetTypes().Select(t => t.Name).ToList();

            Assert.That(typeNames, Has.None.EqualTo("ChemicalCaseCompleted"));
            Assert.That(typeNames, Has.None.EqualTo("ChemicalCaseCompletedHandler"));
        }

        [Test]
        public void SearchListJob_NoLongerBuildsChemicalCasesOrReports()
        {
            var methodNames = typeof(SearchListJob)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => m.Name)
                .ToList();

            Assert.That(methodNames, Has.None.EqualTo("ModifyChemicalMainElement"));
            Assert.That(methodNames, Has.None.EqualTo("GenerateProductList"));
        }

        [Test]
        public void KemiKontrolReportResourcesAreGone()
        {
            Assert.That(ServiceAssembly.GetManifestResourceNames(), Has.None.Contains("KemiKontrol_rapport"));
        }
    }
}
