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
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using ChemicalsBase.Infrastructure.Data.Entities;
    using Microting.eForm.Infrastructure.Constants;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

    /// <summary>
    /// Hardening of the 02:00 chemical register sync in SearchListJob
    /// ("chemicalbase updates"). A bad central feed must never wipe tenant
    /// data. The whole sync is keyed on RemoteId. <see cref="ChemicalFeedGuard.RunAsync"/>
    /// is exercised with delegates standing in for HTTP and EF; this project
    /// has no database harness (see <see cref="AdhocReminderRecipientTests"/>),
    /// so the EF lambdas inside SearchListJob are not covered here.
    /// </summary>
    [TestFixture]
    public class ChemicalFeedGuardTests
    {
        private const int HealthyLocalCount = 5400;

        private static Chemical Local(int id, string remoteId, string workflowState = null) => new()
        {
            Id = id,
            RemoteId = remoteId,
            RegistrationNo = $"reg-{id}",
            Name = $"Chemical {id}",
            WorkflowState = workflowState ?? Constants.WorkflowStates.Created
        };

        private static string FeedJson(IEnumerable<string> remoteIds) =>
            JsonSerializer.Serialize(remoteIds.Select((remoteId, n) => new
            {
                remoteId,
                registrationNo = $"reg-{n}",
                name = $"Chemical {n}"
            }));

        private static string DistinctFeed(int size) =>
            FeedJson(Enumerable.Range(1, size).Select(n => $"remote-{n}"));

        /// <summary>Runs the guard with recording delegates instead of HTTP and EF.</summary>
        private sealed class Harness
        {
            public readonly ConcurrentBag<string> Upserted = new();
            public readonly List<IReadOnlySet<string>> RemoveCalls = new();
            public int LoadCalls;

            /// <param name="localActiveCount">Local active chemicals "remote-1".."remote-N",
            /// unless <paramref name="localRemoteIds"/> is given.</param>
            public Task<ChemicalSyncOutcome> Run(
                string body,
                HttpStatusCode status = HttpStatusCode.OK,
                int localActiveCount = HealthyLocalCount,
                Func<Chemical, ValueTask> upsert = null,
                IReadOnlyCollection<string> localRemoteIds = null)
            {
                localRemoteIds ??= Enumerable.Range(1, localActiveCount).Select(n => $"remote-{n}").ToList();
                return ChemicalFeedGuard.RunAsync(
                    () => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }),
                    () =>
                    {
                        Interlocked.Increment(ref LoadCalls);
                        return Task.FromResult(localRemoteIds);
                    },
                    async (chemical, _) =>
                    {
                        if (upsert != null)
                        {
                            await upsert(chemical);
                        }

                        await Task.Yield();
                        Upserted.Add(chemical.RemoteId);
                    },
                    feedRemoteIds =>
                    {
                        RemoveCalls.Add(feedRemoteIds);
                        return Task.CompletedTask;
                    },
                    new ParallelOptions { MaxDegreeOfParallelism = 64 });
            }
        }

        // ---- RunAsync: removal is never reached on a bad feed -------------

        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.NotFound)]
        [TestCase(HttpStatusCode.BadGateway)]
        public async Task NonSuccessStatus_AppliesNothing(HttpStatusCode status)
        {
            var harness = new Harness();

            var outcome = await harness.Run(DistinctFeed(5000), status);

            Assert.That(harness.Upserted, Is.Empty);
            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(harness.LoadCalls, Is.Zero);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.HttpFailure));
        }

        [Test]
        public async Task NullBody_AppliesNothing()
        {
            var harness = new Harness();

            var outcome = await harness.Run("null");

            Assert.That(harness.Upserted, Is.Empty);
            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.NoFeed));
        }

        [TestCase(0)]
        [TestCase(5)]
        [TestCase(HealthyLocalCount)]
        public async Task EmptyFeed_NeverRemoves(int localActiveCount)
        {
            var harness = new Harness();

            var outcome = await harness.Run("[]", localActiveCount: localActiveCount);

            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.EmptyFeed));
        }

        [Test]
        public async Task FeedWithOnlyNullRemoteIds_NeverUpsertsOrRemoves()
        {
            var harness = new Harness();

            var outcome = await harness.Run(FeedJson(Enumerable.Repeat<string>(null, 4520)));

            Assert.That(harness.Upserted, Is.Empty);
            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.PartialFeed));
        }

        [Test]
        public async Task RightSizedFeedWithFewDistinctRemoteIds_NeverRemoves()
        {
            var harness = new Harness();
            var remoteIds = Enumerable.Range(0, 4520).Select(n => $"remote-{n % 10}");

            var outcome = await harness.Run(FeedJson(remoteIds));

            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.PartialFeed));
            Assert.That(outcome.FeedRemoteIds, Is.EqualTo(10));
        }

        [Test]
        public async Task FeedBelowHalfOfLocal_UpsertsButNeverRemoves()
        {
            var harness = new Harness();

            var outcome = await harness.Run(DistinctFeed(HealthyLocalCount / 2 - 1));

            Assert.That(harness.Upserted, Has.Count.EqualTo(HealthyLocalCount / 2 - 1));
            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.PartialFeed));
        }

        [Test]
        public async Task FailureWhileUpserting_Propagates_AndNeverRemoves()
        {
            var harness = new Harness();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await harness.Run(DistinctFeed(1000), localActiveCount: 1000, upsert: chemical => chemical.RemoteId == "remote-500"
                    ? throw new InvalidOperationException("boom")
                    : ValueTask.CompletedTask));

            Assert.That(harness.RemoveCalls, Is.Empty);
        }

        // ---- RunAsync: unkeyed local rows are not counted ------------------

        private static List<string> KeyedAndUnkeyedLocals(int unkeyed, int keyed) =>
            Enumerable.Range(1, keyed).Select(n => $"remote-{n}")
                .Concat(Enumerable.Range(0, unkeyed).Select(n => n % 2 == 0 ? null : "  "))
                .ToList();

        [TestCase(600, 400)]
        [TestCase(900, 100)]
        public async Task UnkeyedLocalRows_DoNotCountTowardsTheRatios(int unkeyed, int keyed)
        {
            var harness = new Harness();

            var outcome = await harness.Run(
                DistinctFeed(keyed), localRemoteIds: KeyedAndUnkeyedLocals(unkeyed, keyed));

            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.None));
            Assert.That(outcome.LocalActiveCount, Is.EqualTo(keyed));
            Assert.That(harness.RemoveCalls, Has.Count.EqualTo(1));
        }

        // ---- CopyIdentity ---------------------------------------------------

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("\t\n")]
        public void CopyIdentity_BlankFeedValues_KeepTheLocalValues(string blank)
        {
            var local = Local(1, "remote-1");
            var feed = new Chemical { RemoteId = "remote-1", RegistrationNo = blank, Name = blank };

            ChemicalFeedGuard.CopyIdentity(local, feed);

            Assert.That(local.RegistrationNo, Is.EqualTo("reg-1"));
            Assert.That(local.Name, Is.EqualTo("Chemical 1"));
        }

        [Test]
        public void CopyIdentity_RealFeedValues_Overwrite()
        {
            var local = Local(1, "remote-1");
            var feed = new Chemical { RemoteId = "remote-1", RegistrationNo = "new-reg", Name = "New name" };

            ChemicalFeedGuard.CopyIdentity(local, feed);

            Assert.That(local.RegistrationNo, Is.EqualTo("new-reg"));
            Assert.That(local.Name, Is.EqualTo("New name"));
        }

        // ---- RunAsync: normal feed ----------------------------------------

        [Test]
        public async Task NormalFeed_UpsertsEveryChemicalInParallel_AndRemovesAgainstTheFullRemoteIdSet()
        {
            const int feedSize = 5000;
            var harness = new Harness();

            var outcome = await harness.Run(DistinctFeed(feedSize), localActiveCount: feedSize);

            var expected = Enumerable.Range(1, feedSize).Select(n => $"remote-{n}").ToList();
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.None));
            Assert.That(harness.Upserted, Is.EquivalentTo(expected));
            Assert.That(harness.RemoveCalls, Has.Count.EqualTo(1));
            Assert.That(harness.RemoveCalls[0], Is.EquivalentTo(expected));
        }

        [Test]
        public async Task DuplicateRemoteIdsInFeed_AreUpsertedOnce()
        {
            var harness = new Harness();
            var remoteIds = Enumerable.Range(1, 300).SelectMany(n => new[] { $"remote-{n}", $"remote-{n}" });

            await harness.Run(FeedJson(remoteIds), localActiveCount: 300);

            Assert.That(harness.Upserted, Has.Count.EqualTo(300));
            Assert.That(harness.RemoveCalls.Single(), Has.Count.EqualTo(300));
        }

        // ---- RunAsync: mass-removal guard ---------------------------------

        private static IEnumerable<string> Ids(string prefix, int from, int count) =>
            Enumerable.Range(from, count).Select(n => $"{prefix}-{n}");

        [Test]
        public async Task RightSizedFeedWithDisjointRemoteIds_NeverRemoves()
        {
            var harness = new Harness();

            var outcome = await harness.Run(FeedJson(Ids("other", 1, 1000)), localActiveCount: 1000);

            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.MassRemoval));
            Assert.That(outcome.Decision.ApplyUpserts, Is.True);
        }

        [Test]
        public async Task FeedMissing49PercentOfLocal_Removes()
        {
            var harness = new Harness();
            var feed = Ids("remote", 1, 510).Concat(Ids("other", 1, 490));

            var outcome = await harness.Run(FeedJson(feed), localActiveCount: 1000);

            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.None));
            Assert.That(harness.RemoveCalls, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task FeedMissing51PercentOfLocal_NeverRemoves()
        {
            var harness = new Harness();
            var feed = Ids("remote", 1, 490).Concat(Ids("other", 1, 510));

            var outcome = await harness.Run(FeedJson(feed), localActiveCount: 1000);

            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.MassRemoval));
        }

        [Test]
        public void RemovingExactlyHalf_IsAllowed_OneMoreIsMassRemoval()
        {
            var local = ChemicalFeedGuard.RatioCheckMinimumLocalCount * 10;
            var half = (int)(local * ChemicalFeedGuard.MaximumRemovalRatio);

            Assert.That(ChemicalFeedGuard.Decide(feedRows: local, feedRemoteIds: local, localActiveCount: local, plannedRemovals: half).ApplyRemovals, Is.True);
            Assert.That(ChemicalFeedGuard.Decide(feedRows: local, feedRemoteIds: local, localActiveCount: local, plannedRemovals: half + 1).Skip, Is.EqualTo(ChemicalSyncSkip.MassRemoval));
        }

        [Test]
        public void SmallTenantBelowRatioCheckMinimum_MayRemoveMostChemicals()
        {
            var local = ChemicalFeedGuard.RatioCheckMinimumLocalCount - 1;

            Assert.That(ChemicalFeedGuard.Decide(feedRows: 10, feedRemoteIds: 10, localActiveCount: local, plannedRemovals: local - 1).ApplyRemovals, Is.True);
        }

        [Test]
        public async Task LocalsWithoutRemoteId_DoNotCountTowardsMassRemoval()
        {
            var harness = new Harness();
            var locals = Ids("remote", 1, 500).Concat(Enumerable.Repeat<string>(null, 500)).ToList();

            var outcome = await harness.Run(FeedJson(Ids("remote", 1, 500)), localRemoteIds: locals);

            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.None));
            Assert.That(outcome.PlannedRemovals, Is.Zero);
        }

        // ---- RemoteId comparison matches the DB collation (case-insensitive, trimmed)

        [Test]
        public async Task FeedRemoteIdDifferingOnlyInCaseOrWhitespace_DoesNotRemoveTheLocalChemical()
        {
            var harness = new Harness();

            await harness.Run(FeedJson(new[] { "REMOTE-1", " remote-2 " }), localActiveCount: 2);

            var locals = new[] { Local(1, "remote-1"), Local(2, "Remote-2") };
            Assert.That(ChemicalFeedGuard.SelectToRemove(harness.RemoveCalls.Single(), locals), Is.Empty);
        }

        [Test]
        public void SelectToRemove_ComparesRemoteIdsCaseInsensitivelyAndTrimmed_WhateverTheSetComparer()
        {
            var feed = new HashSet<string>(StringComparer.Ordinal) { "REMOTE-1", "remote-2 " };
            var locals = new[] { Local(1, " remote-1"), Local(2, "Remote-2"), Local(3, "remote-3") };

            var removed = ChemicalFeedGuard.SelectToRemove(feed, locals);

            Assert.That(removed.Select(x => x.Id), Is.EquivalentTo(new[] { 3 }));
        }

        [Test]
        public async Task RemoteIdsDifferingOnlyInCaseOrWhitespace_AreUpsertedOnce_Trimmed()
        {
            var harness = new Harness();

            await harness.Run(FeedJson(new[] { "remote-1", "REMOTE-1", " remote-1 " }), localActiveCount: 1);

            Assert.That(harness.Upserted, Is.EquivalentTo(new[] { "remote-1" }));
            Assert.That(harness.RemoveCalls.Single(), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task WhitespaceOnlyRemoteIds_CountAsMissing()
        {
            var harness = new Harness();

            var outcome = await harness.Run(FeedJson(Enumerable.Repeat("   ", 200)), localActiveCount: 200);

            Assert.That(harness.Upserted, Is.Empty);
            Assert.That(harness.RemoveCalls, Is.Empty);
            Assert.That(outcome.Decision.Skip, Is.EqualTo(ChemicalSyncSkip.PartialFeed));
        }

        // ---- Decide -------------------------------------------------------

        [Test]
        public void FeedAtHalfOfLocal_AllowsRemovals()
        {
            var local = ChemicalFeedGuard.RatioCheckMinimumLocalCount * 10;
            var atHalf = (int)(local * ChemicalFeedGuard.MinimumFeedToLocalRatio);

            Assert.That(ChemicalFeedGuard.Decide(feedRows: atHalf, feedRemoteIds: atHalf, localActiveCount: local).ApplyRemovals, Is.True);
            Assert.That(ChemicalFeedGuard.Decide(feedRows: atHalf, feedRemoteIds: atHalf - 1, localActiveCount: local).ApplyRemovals, Is.False);
        }

        [Test]
        public void SmallTenantBelowRatioCheckMinimum_AllowsRemovals()
        {
            var local = ChemicalFeedGuard.RatioCheckMinimumLocalCount - 1;

            var decision = ChemicalFeedGuard.Decide(feedRows: 10, feedRemoteIds: 10, localActiveCount: local);

            Assert.That(decision.ApplyUpserts, Is.True);
            Assert.That(decision.ApplyRemovals, Is.True);
        }

        [Test]
        public void EmptyFeed_KeepsUpsertsAllowed()
        {
            var decision = ChemicalFeedGuard.Decide(feedRows: 0, feedRemoteIds: 0, localActiveCount: HealthyLocalCount);

            Assert.That(decision.ApplyUpserts, Is.True);
            Assert.That(decision.ApplyRemovals, Is.False);
        }

        // ---- SelectToRemove (keyed on RemoteId) ---------------------------

        [Test]
        public void OnlyActiveLocalsWhoseRemoteIdIsMissingFromFeed_AreRemoved()
        {
            var feed = new HashSet<string> { "remote-1", "remote-2" };
            var locals = new[] { Local(1, "remote-1"), Local(2, "remote-2"), Local(3, "remote-3") };

            var removed = ChemicalFeedGuard.SelectToRemove(feed, locals);

            Assert.That(removed.Select(x => x.Id), Is.EquivalentTo(new[] { 3 }));
        }

        [Test]
        public void LocalsWithoutRemoteId_AreNeverRemoved()
        {
            var feed = new HashSet<string> { "remote-1" };
            var locals = new[] { Local(1, "remote-1"), Local(2, null), Local(3, "") };

            Assert.That(ChemicalFeedGuard.SelectToRemove(feed, locals), Is.Empty);
        }

        // ---- PickLocalMatch + TryRestore (non-sticky removal) -------------

        [Test]
        public void PreviouslyRemovedChemicalPresentInFeed_IsRestored()
        {
            var match = ChemicalFeedGuard.PickLocalMatch([Local(1, "remote-1", Constants.WorkflowStates.Removed)]);

            Assert.That(ChemicalFeedGuard.TryRestore(match), Is.True);
            Assert.That(match.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Created));
        }

        [Test]
        public void ActiveTwinIsPreferred_SoTheRemovedDuplicateStaysRemoved()
        {
            var removed = Local(1, "remote-1", Constants.WorkflowStates.Removed);
            var active = Local(2, "remote-1");

            var match = ChemicalFeedGuard.PickLocalMatch([removed, active]);

            Assert.That(match, Is.SameAs(active));
            Assert.That(ChemicalFeedGuard.TryRestore(match), Is.False);
            Assert.That(removed.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Removed));
        }

        [Test]
        public void NoLocalRows_NoMatch()
        {
            Assert.That(ChemicalFeedGuard.PickLocalMatch([]), Is.Null);
        }
    }
}
