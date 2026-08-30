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
    using FirebaseAdmin.Messaging;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

    /// <summary>
    /// A batch in which EVERY message came back SenderIdMismatch means the
    /// sender is holding the wrong Firebase credential, not that every token
    /// is dead. Pruning on that shape would soft-delete a tenant's entire
    /// adhoc DeviceTokens set in one hourly tick, so
    /// <see cref="AdhocReminderJob.IsCredentialFaultBatch"/> vetoes the prune.
    ///
    /// These tests run the shipped decision rule directly, over the batch's
    /// error codes. What they do NOT cover, and cannot without a live
    /// FirebaseMessaging client and a DbContext: that the send loop actually
    /// consults this rule before pruning, that the vetoed messages are counted
    /// as transient failures, and that the Sentry captures fire. Those live in
    /// AdhocReminderJob.SendReminderForTask, which is unreachable from this
    /// project — it needs a real FCM round trip to produce a SendResponse
    /// (SendResponse has no public constructor) and a real DbContext to
    /// soft-delete against.
    /// </summary>
    [TestFixture]
    public class AdhocCredentialFaultBatchTests
    {
        private static readonly MessagingErrorCode? Ok = null;

        [Test]
        public void EveryMessageMismatched_IsACredentialFault()
        {
            var outcomes = new List<MessagingErrorCode?>
            {
                MessagingErrorCode.SenderIdMismatch,
                MessagingErrorCode.SenderIdMismatch,
                MessagingErrorCode.SenderIdMismatch
            };

            Assert.That(AdhocReminderJob.IsCredentialFaultBatch(outcomes), Is.True);
        }

        [Test]
        public void ASingleMismatchedMessage_IsACredentialFault()
        {
            // One token, one mismatch: indistinguishable from the whole-batch
            // shape, so it gets the same benefit of the doubt and survives.
            var outcomes = new List<MessagingErrorCode?> { MessagingErrorCode.SenderIdMismatch };

            Assert.That(AdhocReminderJob.IsCredentialFaultBatch(outcomes), Is.True);
        }

        [Test]
        public void OneSuccessAmongMismatches_IsNotACredentialFault()
        {
            // The credential clearly works, so the mismatching tokens really
            // are foreign-app rows and the per-token prune should run.
            var outcomes = new List<MessagingErrorCode?>
            {
                MessagingErrorCode.SenderIdMismatch,
                Ok,
                MessagingErrorCode.SenderIdMismatch
            };

            Assert.That(AdhocReminderJob.IsCredentialFaultBatch(outcomes), Is.False);
        }

        [Test]
        public void MismatchMixedWithAnotherFailure_IsNotACredentialFault()
        {
            var outcomes = new List<MessagingErrorCode?>
            {
                MessagingErrorCode.SenderIdMismatch,
                MessagingErrorCode.Unregistered
            };

            Assert.That(AdhocReminderJob.IsCredentialFaultBatch(outcomes), Is.False);
        }

        [Test]
        public void AllUnregistered_IsNotACredentialFault()
        {
            // Routine dead-token churn must keep pruning.
            var outcomes = new List<MessagingErrorCode?>
            {
                MessagingErrorCode.Unregistered,
                MessagingErrorCode.Unregistered
            };

            Assert.That(AdhocReminderJob.IsCredentialFaultBatch(outcomes), Is.False);
        }

        [Test]
        public void AllSucceeded_IsNotACredentialFault()
        {
            var outcomes = new List<MessagingErrorCode?> { Ok, Ok };

            Assert.That(AdhocReminderJob.IsCredentialFaultBatch(outcomes), Is.False);
        }

        [Test]
        public void EmptyBatch_IsNotACredentialFault()
        {
            Assert.That(
                AdhocReminderJob.IsCredentialFaultBatch(new List<MessagingErrorCode?>()),
                Is.False);
        }
    }
}
