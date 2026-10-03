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
    using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
    using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

    /// <summary>
    /// #588: a two-worker task completed by the worker whose case is NOT the one stored
    /// on the compliance. eFormCompletedHandler calls the shipped
    /// <see cref="ComplianceCompletion.PointAtCompletedCase"/> right before
    /// <c>Delete()</c>; this project has no database harness (see
    /// <see cref="HiddenOverdueCaseRetractorTests"/>), so the repoint decision is covered in
    /// memory and the handler's own lookups (and the SDK status read) are not.
    /// </summary>
    [TestFixture]
    public class ComplianceCompletionTests
    {
        private const int PlanningCaseId = 40;
        private const int WorkerACaseId = 101;
        private const int WorkerBCaseId = 102;

        private static PlanningCaseSite CompletedByWorkerB() => new()
        {
            PlanningCaseId = PlanningCaseId,
            MicrotingSdkCaseId = WorkerBCaseId,
            Status = 100
        };

        [Test]
        public void CompletedBySecondWorker_CompliancePointsAtTheCompletedCase()
        {
            var compliance = new Compliance { MicrotingSdkCaseId = WorkerACaseId, PlanningCaseSiteId = PlanningCaseId };

            ComplianceCompletion.PointAtCompletedCase(compliance, CompletedByWorkerB(), storedCaseCompleted: false);

            Assert.That(compliance.MicrotingSdkCaseId, Is.EqualTo(WorkerBCaseId),
                "Worker A's case is retracted; the compliance must point at Worker B's completed case.");
            Assert.That(compliance.PlanningCaseSiteId, Is.EqualTo(PlanningCaseId));
        }

        [Test]
        public void ComplianceFoundByDeadline_WithoutPlanningCaseLink_GetsTheLink()
        {
            var compliance = new Compliance { MicrotingSdkCaseId = WorkerACaseId, PlanningCaseSiteId = 0 };

            ComplianceCompletion.PointAtCompletedCase(compliance, CompletedByWorkerB(), storedCaseCompleted: false);

            Assert.That(compliance.MicrotingSdkCaseId, Is.EqualTo(WorkerBCaseId));
            Assert.That(compliance.PlanningCaseSiteId, Is.EqualTo(PlanningCaseId));
        }

        [Test]
        public void ExistingPlanningCaseLink_IsNotRewritten()
        {
            var compliance = new Compliance { MicrotingSdkCaseId = WorkerACaseId, PlanningCaseSiteId = 7 };

            ComplianceCompletion.PointAtCompletedCase(compliance, CompletedByWorkerB(), storedCaseCompleted: false);

            Assert.That(compliance.PlanningCaseSiteId, Is.EqualTo(7));
        }

        [Test]
        public void StoredCaseAlreadyCompleted_KeepsItsCase()
        {
            // Worker A completed first; Worker B's later completion must not take the
            // occurrence over — the completion processed first keeps it.
            var compliance = new Compliance { MicrotingSdkCaseId = WorkerACaseId, PlanningCaseSiteId = 0 };

            ComplianceCompletion.PointAtCompletedCase(compliance, CompletedByWorkerB(), storedCaseCompleted: true);

            Assert.That(compliance.MicrotingSdkCaseId, Is.EqualTo(WorkerACaseId));
            Assert.That(compliance.PlanningCaseSiteId, Is.EqualTo(0), "nothing is touched");
        }
    }
}
