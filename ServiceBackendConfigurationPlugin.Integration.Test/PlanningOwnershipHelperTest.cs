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
    using System.Threading.Tasks;
    using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

    /// <summary>
    /// Covers the ownership guard added for issue #1262: the nightly ShowExpireDate sweep in
    /// SearchListJob must never touch a planning that a live AreaRulePlanning points at.
    /// </summary>
    [TestFixture]
    public class PlanningOwnershipHelperTest
    {
        private static List<Planning> Plannings(params int[] ids)
        {
            return ids.Select(id => new Planning { Id = id }).ToList();
        }

        [Test]
        public async Task ExcludeBackendConfigurationOwnedAsync_KeepsUnownedPlanning_AndDropsOwnedPlanning()
        {
            var candidates = Plannings(1, 2, 3);

            var survivors = await PlanningOwnershipHelper.ExcludeBackendConfigurationOwnedAsync(
                candidates,
                _ => Task.FromResult(new List<int> { 2 }));

            Assert.That(survivors.Select(x => x.Id), Is.EquivalentTo(new[] { 1, 3 }),
                "A backend-configuration owned planning must survive the sweep, an unowned one must still be processed.");
        }

        [Test]
        public async Task ExcludeBackendConfigurationOwnedAsync_AllOwned_ReturnsNothingToProcess()
        {
            var candidates = Plannings(10, 11);

            var survivors = await PlanningOwnershipHelper.ExcludeBackendConfigurationOwnedAsync(
                candidates,
                batch => Task.FromResult(batch.ToList()));

            Assert.That(survivors, Is.Empty);
        }

        [Test]
        public async Task ExcludeBackendConfigurationOwnedAsync_NoneOwned_ReturnsEveryCandidate()
        {
            var candidates = Plannings(10, 11, 12);

            var survivors = await PlanningOwnershipHelper.ExcludeBackendConfigurationOwnedAsync(
                candidates,
                _ => Task.FromResult(new List<int>()));

            Assert.That(survivors.Select(x => x.Id), Is.EqualTo(new[] { 10, 11, 12 }));
        }

        [Test]
        public async Task ExcludeBackendConfigurationOwnedAsync_EmptyCandidates_NeverQueries()
        {
            var calls = 0;

            var survivors = await PlanningOwnershipHelper.ExcludeBackendConfigurationOwnedAsync(
                new List<Planning>(),
                _ =>
                {
                    calls++;
                    return Task.FromResult(new List<int>());
                });

            Assert.Multiple(() =>
            {
                Assert.That(survivors, Is.Empty);
                Assert.That(calls, Is.Zero);
            });
        }

        [Test]
        public async Task ExcludeBackendConfigurationOwnedAsync_BatchesLookupsAndUnionsResults()
        {
            var candidates = Plannings(Enumerable.Range(1, 1200).ToArray());
            var batchSizes = new List<int>();

            var survivors = await PlanningOwnershipHelper.ExcludeBackendConfigurationOwnedAsync(
                candidates,
                batch =>
                {
                    batchSizes.Add(batch.Count);
                    // Owned: the first id of every batch.
                    return Task.FromResult(new List<int> { batch.First() });
                },
                500);

            Assert.Multiple(() =>
            {
                Assert.That(batchSizes, Is.EqualTo(new[] { 500, 500, 200 }),
                    "Ids must be sent in batches so a large candidate set cannot exceed the parameter limit.");
                Assert.That(survivors, Has.Count.EqualTo(1197));
                Assert.That(survivors.Select(x => x.Id),
                    Does.Not.Contain(1).And.Not.Contain(501).And.Not.Contain(1001));
            });
        }

        [Test]
        public void BatchPlanningIds_SplitsOnExactMultiple_WithoutTrailingEmptyBatch()
        {
            var batches = PlanningOwnershipHelper
                .BatchPlanningIds(Enumerable.Range(1, 1000).ToList(), 500)
                .ToList();

            Assert.That(batches.Select(x => x.Count), Is.EqualTo(new[] { 500, 500 }));
        }

        /// <summary>
        /// Stands in for the shared ItemsPlanningPnDbContext. A rejected save leaves the offending
        /// entity tracked as Modified, so every later SaveChangesAsync bundles it in and throws
        /// again - until the change tracker is cleared. <see cref="Clear"/> is the ChangeTracker.Clear().
        /// </summary>
        private sealed class PoisonableContext
        {
            private bool _poisonedByEarlierSave;

            public List<int> Saved { get; } = new List<int>();

            public int ClearCount { get; private set; }

            public void Save(int planningId, bool rejected)
            {
                if (_poisonedByEarlierSave)
                {
                    throw new InvalidOperationException(
                        "The entity rejected by an earlier save is still tracked as Modified.");
                }

                if (rejected)
                {
                    _poisonedByEarlierSave = true;
                    throw new InvalidOperationException($"Planning {planningId} was rejected.");
                }

                Saved.Add(planningId);
            }

            public void Clear()
            {
                ClearCount++;
                _poisonedByEarlierSave = false;
            }
        }

        private static Func<int, Task<string>> ProcessAgainst(PoisonableContext context, int failingPlanningId)
        {
            return planningId =>
            {
                context.Save(planningId, planningId == failingPlanningId);
                return Task.FromResult($"planning-{planningId}");
            };
        }

        [Test]
        public async Task ProcessPlanningsIndividuallyAsync_FailureOnOnePlanning_StillProcessesTheRest()
        {
            var context = new PoisonableContext();

            var results = await PlanningOwnershipHelper.ProcessPlanningsIndividuallyAsync(
                new[] { 1, 2, 3, 4 },
                ProcessAgainst(context, 2),
                context.Clear);

            Assert.Multiple(() =>
            {
                Assert.That(context.Saved, Is.EqualTo(new[] { 1, 3, 4 }),
                    "A planning that would have been deleted successfully must still be deleted after an earlier failure.");
                Assert.That(results.Where(x => !x.Succeeded).Select(x => x.PlanningId), Is.EqualTo(new[] { 2 }),
                    "Only the planning that actually failed may be reported as failed.");
            });
        }

        [Test]
        public async Task ProcessPlanningsIndividuallyAsync_AttributesEachFailureToThePlanningThatFailed()
        {
            var context = new PoisonableContext();

            var results = await PlanningOwnershipHelper.ProcessPlanningsIndividuallyAsync(
                new[] { 1, 2, 3 },
                ProcessAgainst(context, 2),
                context.Clear);

            Assert.Multiple(() =>
            {
                Assert.That(results.Select(x => x.PlanningId), Is.EqualTo(new[] { 1, 2, 3 }));
                Assert.That(results[0].PlanningName, Is.EqualTo("planning-1"));
                Assert.That(results[1].FailureException.Message, Does.Contain("Planning 2 was rejected"),
                    "The reported exception must be the one this planning threw, not a knock-on from another planning.");
                Assert.That(results[2].PlanningName, Is.EqualTo("planning-3"),
                    "A planning processed after a failure must be reported as succeeded, not as collateral damage.");
            });
        }

        [Test]
        public async Task ProcessPlanningsIndividuallyAsync_WithoutReset_WouldCascadeToEveryLaterPlanning()
        {
            // Guards the fake: this is the behaviour the reset exists to prevent. Without it, one
            // root cause is misattributed to every planning that follows it.
            var context = new PoisonableContext();

            var results = await PlanningOwnershipHelper.ProcessPlanningsIndividuallyAsync(
                new[] { 1, 2, 3, 4 },
                ProcessAgainst(context, 2),
                () => { });

            Assert.Multiple(() =>
            {
                Assert.That(context.Saved, Is.EqualTo(new[] { 1 }));
                Assert.That(results.Where(x => !x.Succeeded).Select(x => x.PlanningId), Is.EqualTo(new[] { 2, 3, 4 }));
            });
        }

        [Test]
        public async Task ProcessPlanningsIndividuallyAsync_ResetsAfterEveryPlanning_SoTheTrackerStaysBounded()
        {
            var context = new PoisonableContext();

            await PlanningOwnershipHelper.ProcessPlanningsIndividuallyAsync(
                new[] { 1, 2, 3 },
                ProcessAgainst(context, -1),
                context.Clear);

            Assert.That(context.ClearCount, Is.EqualTo(3));
        }

        [Test]
        public async Task ProcessPlanningsIndividuallyAsync_NoPlannings_DoesNothing()
        {
            var context = new PoisonableContext();

            var results = await PlanningOwnershipHelper.ProcessPlanningsIndividuallyAsync(
                new List<int>(),
                ProcessAgainst(context, -1),
                context.Clear);

            Assert.Multiple(() =>
            {
                Assert.That(results, Is.Empty);
                Assert.That(context.ClearCount, Is.Zero);
            });
        }
    }
}
