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
    /// The adhoc sender must only ever see tokens minted by the adhoc app.
    /// flutter-adhoc and flutter-eform register into the SAME DeviceTokens
    /// table but use DIFFERENT Firebase projects, so pushing an eform-minted
    /// token through the adhoc credential returns SenderIdMismatch. That is
    /// counted as a transient failure, which leaves the task's
    /// Last*ReminderSentAt marker unset — the reminder then retries every
    /// hour forever and re-pushes every healthy recipient each time.
    ///
    /// These tests run the REAL recipient predicate:
    /// <see cref="AdhocReminderJob.SelectRecipientTokens"/> is the exact
    /// IQueryable composition the send path executes, evaluated here over an
    /// in-memory queryable instead of MariaDB. Re-stating the predicate in
    /// the test instead would assert nothing about the shipped query.
    ///
    /// This project has no database harness (no Testcontainers, no
    /// DbContext fixture — see AdhocReminderEvaluatorTests, a plain
    /// dependency-free [TestFixture]). Sharing the expression is the
    /// smallest way to exercise the shipped predicate rather than a copy of
    /// it. What is deliberately NOT covered here is EF's SQL translation and
    /// index selection; that needs a live MariaDB and belongs to bc-plugin's
    /// integration suite.
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

        [Test]
        public void NoRecipientSites_SelectsNothing()
        {
            var all = new List<DeviceToken> { Token("adhoc", 16, "adhoc-tok") };

            Assert.That(SelectedTokens(all), Is.Empty);
        }

        [Test]
        public void AdhocAppIdConstant_IsExactlyAdhoc()
        {
            Assert.That(AdhocReminderJob.AppId, Is.EqualTo("adhoc"));
        }
    }
}
