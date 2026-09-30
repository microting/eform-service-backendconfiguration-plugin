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
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microting.eForm.Infrastructure.Constants;
    using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
    using CaseRow = ServiceBackendConfigurationPlugin.Infrastructure.Helpers.HiddenOverdueCaseRetractor.SdkCaseRow;
    using ComplianceRow = ServiceBackendConfigurationPlugin.Infrastructure.Helpers.HiddenOverdueCaseRetractor.ComplianceCaseRow;

    /// <summary>
    /// Selection for the #1325 device cleanup. Runs the shipped pure
    /// <see cref="HiddenOverdueCaseRetractor.SelectCasesToRetract"/> and the
    /// shipped <see cref="HiddenOverdueCaseRetractor.SelectHiddenTaskCompliances"/>
    /// predicate over in-memory rows — this project has no database harness
    /// (see <see cref="AdhocReminderRecipientTests"/>). EF's SQL translation of
    /// the loader is not covered here.
    /// </summary>
    [TestFixture]
    public class HiddenOverdueCaseRetractorTests
    {
        private static readonly DateTime Today = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Yesterday = Today.Date.AddDays(-1);
        private static readonly DateTime Tomorrow = Today.Date.AddDays(1);

        private static ComplianceRow Compliance(
            int id, DateTime deadline, int currentCaseId, string workflowState = null, params int[] versionCaseIds)
        {
            return new ComplianceRow(
                id, deadline, workflowState ?? Constants.WorkflowStates.Created, currentCaseId,
                versionCaseIds.Append(currentCaseId).ToList());
        }

        private static CaseRow Case(
            int id, int siteId = 1, int? status = 66, DateTime? doneAt = null, string workflowState = null)
        {
            return new CaseRow(id, 1000 + id, status, doneAt, workflowState ?? Constants.WorkflowStates.Created, siteId);
        }

        private static List<int> Selected(IEnumerable<ComplianceRow> compliances, params CaseRow[] cases)
        {
            return HiddenOverdueCaseRetractor
                .SelectCasesToRetract(compliances.ToList(), cases.ToDictionary(x => x.Id), Today)
                .Select(x => x.Case.Id)
                .ToList();
        }

        [Test]
        public void MissedUncompletedCase_IsSelected()
        {
            Assert.That(Selected([Compliance(1, Yesterday, 10)], Case(10)), Is.EquivalentTo(new[] { 10 }));
        }

        [Test]
        public void CompletedCase_IsNotSelected()
        {
            var compliances = new[] { Compliance(1, Yesterday, 10), Compliance(2, Yesterday, 11) };

            Assert.That(
                Selected(compliances, Case(10, status: 100), Case(11, doneAt: Yesterday)),
                Is.Empty);
        }

        /// <summary>
        /// The fresh read right before each delete: a case answered, removed or gone since
        /// the selection is no longer retracted.
        /// </summary>
        [Test]
        public void CaseChangedSinceTheSelection_IsNoLongerRetractable()
        {
            Assert.Multiple(() =>
            {
                Assert.That(HiddenOverdueCaseRetractor.IsLiveAndUncompleted(Case(10)), Is.True);
                Assert.That(HiddenOverdueCaseRetractor.IsLiveAndUncompleted(Case(10, status: 100)), Is.False);
                Assert.That(HiddenOverdueCaseRetractor.IsLiveAndUncompleted(Case(10, doneAt: Today)), Is.False);
                Assert.That(HiddenOverdueCaseRetractor.IsLiveAndUncompleted(
                    Case(10, workflowState: Constants.WorkflowStates.Removed)), Is.False);
                Assert.That(HiddenOverdueCaseRetractor.IsLiveAndUncompleted(null), Is.False, "deleted meanwhile");
            });
        }

        /// <summary>At most one cloud delete is ever outstanding; local-only cases are never held back.</summary>
        [TestCase(null, 1, true)]
        [TestCase(2_000_000_000, 1, true)]
        [TestCase(1_999_999_999, 1, false)]
        [TestCase(12345, 0, true)]
        [TestCase(12345, 1, false)]
        public void CloudDelete_StartsOnlyWhenNoneIsRunning(int? microtingUid, int running, bool expected)
        {
            Assert.That(HiddenOverdueCaseRetractor.MayStartDelete(microtingUid, running), Is.EqualTo(expected));
        }

        /// <summary>
        /// A cloud case whose delete timed out goes last (the longest-ago timeout first), so it
        /// cannot hold back the other cloud cases day after day; the rest keep their order.
        /// </summary>
        [Test]
        public void CloudCasesThatTimedOut_AreTriedLast()
        {
            // Selection order deliberately not by id; case 14 is local-only (no MicrotingUid).
            var cases = new[] { Case(13), Case(10), new CaseRow(14, null, 66, null, Constants.WorkflowStates.Created, 1), Case(12), Case(11) };
            var candidates = cases
                .Select(c => new HiddenOverdueCaseRetractor.RetractionCandidate(c.Id, Yesterday, c, "missed"))
                .ToList();
            var timedOutAt = new Dictionary<int, DateTime>
            {
                [Case(10).MicrotingUid!.Value] = Today.AddHours(-1),
                [Case(11).MicrotingUid!.Value] = Today.AddDays(-1)
            };

            var ordered = HiddenOverdueCaseRetractor.OrderForRetraction(candidates, timedOutAt)
                .Select(x => x.Case.Id);

            Assert.That(ordered, Is.EqualTo(new[] { 13, 14, 12, 11, 10 }));
        }

        [Test]
        public void AlreadyRemovedOrRetractedCase_IsNotSelected()
        {
            var compliances = new[] { Compliance(1, Yesterday, 10), Compliance(2, Yesterday, 11) };

            Assert.That(
                Selected(compliances,
                    Case(10, workflowState: Constants.WorkflowStates.Removed),
                    Case(11, workflowState: Constants.WorkflowStates.Retracted)),
                Is.Empty);
        }

        [Test]
        public void SoftDeletedCompliance_MissedCase_IsSelected()
        {
            Assert.That(
                Selected([Compliance(1, Yesterday, 10, Constants.WorkflowStates.Removed)], Case(10)),
                Is.EquivalentTo(new[] { 10 }));
        }

        [Test]
        public void MissedCompliance_EveryCaseItEverPointedAt_IsSelected()
        {
            Assert.That(
                Selected([Compliance(1, Yesterday, 11, null, 10)], Case(10), Case(11)),
                Is.EquivalentTo(new[] { 10, 11 }));
        }

        [Test]
        public void TodaysAndTomorrowsOccurrence_IsNotSelected()
        {
            var compliances = new[]
            {
                Compliance(1, Today.Date, 10),
                Compliance(2, Tomorrow, 11),
                Compliance(3, Tomorrow, 12, Constants.WorkflowStates.Removed)
            };

            Assert.That(Selected(compliances, Case(10), Case(11), Case(12)), Is.Empty);
        }

        [Test]
        public void ReviveOrphan_OnSameSite_IsSelected_AndCurrentCaseIsKept()
        {
            var compliances = new[]
            {
                Compliance(1, Today.Date, 11, null, 10),
                Compliance(2, Tomorrow, 21, null, 20)
            };

            Assert.That(
                Selected(compliances, Case(10), Case(11), Case(20), Case(21)),
                Is.EquivalentTo(new[] { 10, 20 }));
        }

        [Test]
        public void ReviveOrphan_OnAnotherSite_IsNotSelected()
        {
            Assert.That(
                Selected([Compliance(1, Tomorrow, 11, null, 10)], Case(10, siteId: 2), Case(11, siteId: 1)),
                Is.Empty);
        }

        [Test]
        public void CaseStillCurrentForALiveFutureOccurrence_IsNeverSelected()
        {
            // Case 10 is in a missed row's history but is the current case of
            // tomorrow's occurrence — it is on the device on purpose.
            var compliances = new[]
            {
                Compliance(1, Yesterday, 9, null, 10),
                Compliance(2, Tomorrow, 10)
            };

            Assert.That(Selected(compliances, Case(9), Case(10)), Is.EquivalentTo(new[] { 9 }));
        }

        [TestCase(null, true)]
        [TestCase(2_000_000_000, true)]
        [TestCase(2_000_012_345, true)]
        [TestCase(1_999_999_999, false)]
        [TestCase(123456, false)]
        public void IsLocalOnly_SyntheticOrMissingMicrotingUid(int? microtingUid, bool expected)
        {
            Assert.That(HiddenOverdueCaseRetractor.IsLocalOnly(microtingUid), Is.EqualTo(expected));
        }

        [Test]
        public void CaseSharedByTwoMissedRows_IsSelectedOnce()
        {
            var compliances = new[]
            {
                Compliance(1, Yesterday, 9, null, 8),
                Compliance(2, Yesterday.AddDays(-1), 8)
            };

            Assert.That(Selected(compliances, Case(8), Case(9)), Is.EquivalentTo(new[] { 8, 9 }));
        }

        [Test]
        public void HiddenTaskCompliances_OnlyComplianceDisabledLiveTasks()
        {
            var areaRulePlannings = new List<AreaRulePlanning>
            {
                new() { ItemPlanningId = 1, ComplianceEnabled = false, WorkflowState = Constants.WorkflowStates.Created },
                new() { ItemPlanningId = 2, ComplianceEnabled = true, WorkflowState = Constants.WorkflowStates.Created },
                new() { ItemPlanningId = 3, ComplianceEnabled = false, WorkflowState = Constants.WorkflowStates.Removed },
                // Planning 4 is shared with a task that reports missed occurrences:
                // its cases must never be retracted.
                new() { ItemPlanningId = 4, ComplianceEnabled = true, WorkflowState = Constants.WorkflowStates.Created },
                new() { ItemPlanningId = 4, ComplianceEnabled = false, WorkflowState = Constants.WorkflowStates.Created },
                // Planning 6 is shared by two hidden tasks, and the enabled one is removed.
                new() { ItemPlanningId = 6, ComplianceEnabled = false, WorkflowState = Constants.WorkflowStates.Created },
                new() { ItemPlanningId = 6, ComplianceEnabled = false, WorkflowState = Constants.WorkflowStates.Created },
                new() { ItemPlanningId = 6, ComplianceEnabled = true, WorkflowState = Constants.WorkflowStates.Removed }
            };
            var compliances = new List<Compliance>
            {
                new() { Id = 101, PlanningId = 1, WorkflowState = Constants.WorkflowStates.Created },
                new() { Id = 102, PlanningId = 1, WorkflowState = Constants.WorkflowStates.Removed },
                new() { Id = 201, PlanningId = 2, WorkflowState = Constants.WorkflowStates.Created },
                new() { Id = 301, PlanningId = 3, WorkflowState = Constants.WorkflowStates.Created },
                new() { Id = 401, PlanningId = 4, WorkflowState = Constants.WorkflowStates.Created },
                new() { Id = 501, PlanningId = 5, WorkflowState = Constants.WorkflowStates.Created },
                new() { Id = 601, PlanningId = 6, WorkflowState = Constants.WorkflowStates.Created }
            };

            var selected = HiddenOverdueCaseRetractor
                .SelectHiddenTaskCompliances(compliances.AsQueryable(), areaRulePlannings.AsQueryable())
                .Select(x => x.Id)
                .ToList();

            Assert.That(selected, Is.EquivalentTo(new[] { 101, 102, 601 }));
        }
    }
}
