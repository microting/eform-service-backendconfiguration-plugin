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
    using System.Collections.Generic;
    using System.Linq;
    using Microting.eForm.Infrastructure.Constants;
    using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

    /// <summary>
    /// The adhoc sender must only ever see tokens minted by the adhoc app —
    /// see <see cref="AdhocReminderJob.AdhocAppId"/> for why the shared
    /// DeviceTokens table makes that a live risk.
    ///
    /// These tests run the REAL recipient predicate:
    /// <see cref="AdhocReminderJob.SelectRecipientTokens"/> is the exact
    /// IQueryable composition the send path executes, evaluated here over an
    /// in-memory queryable instead of MariaDB. Re-stating the predicate in
    /// the test would assert nothing about the shipped query.
    ///
    /// The seam is shared because this project has no database harness (no
    /// Testcontainers, no DbContext fixture — see AdhocReminderEvaluatorTests,
    /// a plain dependency-free [TestFixture]); sharing the expression is the
    /// smallest way to exercise the shipped predicate rather than a copy.
    ///
    /// Deliberately NOT covered here, because LINQ-to-Objects is not MariaDB:
    ///  - EF's SQL translation and index selection. Needs a live MariaDB;
    ///    belongs to bc-plugin's integration suite.
    ///  - String comparison SEMANTICS. String <c>==</c> is ORDINAL and
    ///    CASE-SENSITIVE in LINQ-to-Objects, while the DeviceTokens columns
    ///    carry MariaDB's default case-INSENSITIVE utf8mb4_*_ci collation. A
    ///    production row with <c>AppId = "Adhoc"</c> WOULD be selected by the
    ///    shipped query, whereas
    ///    <see cref="ForeignAppToken_IsNotSelected_EvenForAMatchingSite"/>
    ///    asserts a world in which it would not; the same applies to the
    ///    WorkflowState comparison. This gap belongs on the list precisely
    ///    because it alters the predicate's MEANING, not merely its
    ///    performance.
    /// </summary>
    [TestFixture]
    public class AdhocReminderRecipientTests
    {
        private static DeviceToken Token(
            string appId, int sdkSiteId, string fcmToken, string workflowState = null)
        {
            return new DeviceToken
            {
                AppId = appId,
                InstallationId = $"install-{fcmToken}",
                SdkSiteId = sdkSiteId,
                FcmToken = fcmToken,
                Platform = "android",
                WorkflowState = workflowState ?? Constants.WorkflowStates.Created
            };
        }

        private static List<string> SelectedTokens(
            IEnumerable<DeviceToken> all, params int[] sdkSiteIds)
        {
            return AdhocReminderJob
                .SelectRecipientTokens(all.AsQueryable(), sdkSiteIds.ToList())
                .Select(x => x.FcmToken)
                .ToList();
        }

        [Test]
        public void ForeignAppToken_IsNotSelected_EvenForAMatchingSite()
        {
            var all = new List<DeviceToken>
            {
                Token("adhoc", 10, "adhoc-tok"),
                Token("eform", 10, "eform-tok"),
                Token("time", 10, "time-tok")
            };

            Assert.That(SelectedTokens(all, 10), Is.EquivalentTo(new[] { "adhoc-tok" }));
        }

        [Test]
        public void AdhocToken_ForAMatchingSite_IsSelected()
        {
            var all = new List<DeviceToken> { Token("adhoc", 11, "adhoc-tok") };

            Assert.That(SelectedTokens(all, 11), Is.EquivalentTo(new[] { "adhoc-tok" }));
        }

        [Test]
        public void SoftDeletedToken_IsNotSelected()
        {
            var all = new List<DeviceToken>
            {
                Token("adhoc", 12, "live-tok"),
                Token("adhoc", 12, "dead-tok", Constants.WorkflowStates.Removed)
            };

            Assert.That(SelectedTokens(all, 12), Is.EquivalentTo(new[] { "live-tok" }));
        }

        [Test]
        public void TwoDevicesForOneSite_BothSelected()
        {
            var all = new List<DeviceToken>
            {
                Token("adhoc", 13, "phone-tok"),
                Token("adhoc", 13, "tablet-tok")
            };

            Assert.That(
                SelectedTokens(all, 13),
                Is.EquivalentTo(new[] { "phone-tok", "tablet-tok" }));
        }

        [Test]
        public void TokenForAnotherSite_IsNotSelected()
        {
            var all = new List<DeviceToken>
            {
                Token("adhoc", 14, "mine-tok"),
                Token("adhoc", 15, "theirs-tok")
            };

            Assert.That(SelectedTokens(all, 14), Is.EquivalentTo(new[] { "mine-tok" }));
        }
    }
}
