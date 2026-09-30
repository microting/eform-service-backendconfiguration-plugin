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
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using ChemicalsBase.Infrastructure.Data.Entities;
    using Microting.eForm.Infrastructure.Constants;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

    /// <summary>
    /// Hardening of the 02:00 chemical register sync in SearchListJob
    /// ("chemicalbase updates"). A bad central feed must never wipe tenant
    /// data. Runs the shipped pure <see cref="ChemicalFeedGuard"/> units; this
    /// project has no database harness (see <see cref="AdhocReminderRecipientTests"/>),
    /// so the EF wiring inside SearchListJob is not covered here.
    /// </summary>
    [TestFixture]
    public class ChemicalFeedGuardTests
    {
        private static Chemical FeedChemical(int n, string workflowState = null) => new()
        {
            RemoteId = $"remote-{n}",
            RegistrationNo = $"reg-{n}",
            Name = $"Chemical {n}",
            WorkflowState = workflowState ?? Constants.WorkflowStates.Created
        };

        // ---- Decide: HTTP status ------------------------------------------

        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.NotFound)]
        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.Found)]
        public void NonSuccessStatus_AppliesNothing(HttpStatusCode statusCode)
        {
            var decision = ChemicalFeedGuard.Decide(statusCode, feedCount: 8500, localActiveCount: 8416);

            Assert.That(decision.ApplyUpserts, Is.False);
            Assert.That(decision.ApplyRemovals, Is.False);
            Assert.That(decision.Reason, Does.Contain(((int)statusCode).ToString()));
        }

        [Test]
        public void UnparseableOrNullFeed_AppliesNothing()
        {
            var decision = ChemicalFeedGuard.Decide(HttpStatusCode.OK, feedCount: null, localActiveCount: 8416);

            Assert.That(decision.ApplyUpserts, Is.False);
            Assert.That(decision.ApplyRemovals, Is.False);
        }

        // ---- Decide: empty / partial feed ---------------------------------

        [TestCase(0)]
        [TestCase(5)]
        [TestCase(8416)]
        public void EmptyFeed_SkipsRemovals(int localActiveCount)
        {
            var decision = ChemicalFeedGuard.Decide(HttpStatusCode.OK, feedCount: 0, localActiveCount);

            Assert.That(decision.ApplyRemovals, Is.False);
        }

        [TestCase(10, 8416)]
        [TestCase(4207, 8416)]
        [TestCase(49, 100)]
        public void FeedBelowHalfOfLocal_SkipsRemovalsButKeepsUpserts(int feedCount, int localActiveCount)
        {
            var decision = ChemicalFeedGuard.Decide(HttpStatusCode.OK, feedCount, localActiveCount);

            Assert.That(decision.ApplyUpserts, Is.True);
            Assert.That(decision.ApplyRemovals, Is.False);
            Assert.That(decision.Reason, Is.Not.Empty);
        }

        [TestCase(4208, 8416)]
        [TestCase(50, 100)]
        public void FeedAtHalfOfLocal_AllowsRemovals(int feedCount, int localActiveCount)
        {
            var decision = ChemicalFeedGuard.Decide(HttpStatusCode.OK, feedCount, localActiveCount);

            Assert.That(decision.ApplyUpserts, Is.True);
            Assert.That(decision.ApplyRemovals, Is.True);
        }

        [Test]
        public void SmallTenantBelowRatioCheckMinimum_AllowsRemovals()
        {
            var decision = ChemicalFeedGuard.Decide(HttpStatusCode.OK, feedCount: 10, localActiveCount: 99);

            Assert.That(decision.ApplyUpserts, Is.True);
            Assert.That(decision.ApplyRemovals, Is.True);
        }

        [Test]
        public void NormalFeed_AppliesUpsertsAndRemovals()
        {
            var decision = ChemicalFeedGuard.Decide(HttpStatusCode.OK, feedCount: 8557, localActiveCount: 8416);

            Assert.That(decision.ApplyUpserts, Is.True);
            Assert.That(decision.ApplyRemovals, Is.True);
        }

        [Test]
        public void ThresholdIsHalfOfLocal_AppliedFromOneHundredLocalChemicals()
        {
            Assert.That(ChemicalFeedGuard.MinimumFeedToLocalRatio, Is.EqualTo(0.5));
            Assert.That(ChemicalFeedGuard.RatioCheckMinimumLocalCount, Is.EqualTo(100));
        }

        // ---- SelectIdsToRemove --------------------------------------------

        [Test]
        public void NormalFeed_OnlyLocalChemicalsMissingFromFeedAreRemoved()
        {
            var feedRegistrationNos = new HashSet<string> { "reg-1", "reg-2", "reg-3" };
            var activeLocals = new List<(int Id, string RegistrationNo)>
            {
                (11, "reg-1"), (12, "reg-2"), (13, "reg-3"), (14, "reg-4"), (15, "reg-5")
            };

            var ids = ChemicalFeedGuard.SelectIdsToRemove(feedRegistrationNos, activeLocals);

            Assert.That(ids, Is.EquivalentTo(new[] { 14, 15 }));
        }

        // ---- TryRestore (non-sticky removal) ------------------------------

        [Test]
        public void PreviouslyRemovedChemicalPresentInFeed_IsRestored()
        {
            var local = FeedChemical(1, Constants.WorkflowStates.Removed);
            var feed = FeedChemical(1);

            var restored = ChemicalFeedGuard.TryRestore(local, feed);

            Assert.That(restored, Is.True);
            Assert.That(local.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Created));
        }

        [Test]
        public void ActiveLocalChemical_IsNotTouchedByRestore()
        {
            var local = FeedChemical(1);
            var feed = FeedChemical(1);

            Assert.That(ChemicalFeedGuard.TryRestore(local, feed), Is.False);
            Assert.That(local.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Created));
        }

        [Test]
        public void ChemicalRemovedCentrally_IsNotRestored()
        {
            var local = FeedChemical(1, Constants.WorkflowStates.Removed);
            var feed = FeedChemical(1, Constants.WorkflowStates.Removed);

            Assert.That(ChemicalFeedGuard.TryRestore(local, feed), Is.False);
            Assert.That(local.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Removed));
        }

        // ---- ProcessFeedAsync (parallel, no lost entries) -----------------

        [Test]
        public async Task LargeFeedProcessedInParallel_CollectsEveryRegistrationNo()
        {
            const int feedSize = 50_000;
            var feed = Enumerable.Range(1, feedSize).Select(n => FeedChemical(n)).ToList();
            var processed = 0;

            var registrationNos = await ChemicalFeedGuard.ProcessFeedAsync(
                feed,
                async (_, _) =>
                {
                    await Task.Yield();
                    Interlocked.Increment(ref processed);
                },
                new ParallelOptions { MaxDegreeOfParallelism = 64 });

            Assert.That(processed, Is.EqualTo(feedSize));
            Assert.That(registrationNos, Has.Count.EqualTo(feedSize));
            Assert.That(registrationNos, Is.EquivalentTo(feed.Select(x => x.RegistrationNo)));
        }

        [Test]
        public async Task FailureWhileProcessingFeed_Propagates_SoNoRemovalPhaseRuns()
        {
            var feed = Enumerable.Range(1, 1_000).Select(n => FeedChemical(n)).ToList();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ChemicalFeedGuard.ProcessFeedAsync(
                    feed,
                    (chemical, _) => chemical.RemoteId == "remote-500"
                        ? throw new InvalidOperationException("boom")
                        : ValueTask.CompletedTask,
                    new ParallelOptions { MaxDegreeOfParallelism = 8 }));
        }
    }
}
