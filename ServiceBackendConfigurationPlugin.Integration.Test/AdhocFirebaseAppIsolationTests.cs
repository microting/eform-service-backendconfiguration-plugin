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
    using System.Linq;
    using System.Security.Cryptography;
    using System.Threading;
    using FirebaseAdmin;
    using FirebaseAdmin.Messaging;
    using Google.Apis.Auth.OAuth2;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

    /// <summary>
    /// Why this job must never touch <see cref="FirebaseApp.DefaultInstance"/>
    /// is documented once, next to the code that must not regress:
    /// <see cref="AdhocReminderJob.EnsureAdhocMessaging"/>. The short version —
    /// the default app is a PROCESS-WIDE singleton shared by every service
    /// plugin MicrotingService loads, a second FCM sender (TimePlanning's
    /// PushNotificationService) already claims it, and a collision sends this
    /// job's pushes through the other project with no crash, no dead tokens
    /// and no signal. The only defence is never touching the default app.
    ///
    /// These tests drive the shipped <see cref="AdhocReminderJob"/> entry
    /// point that the hourly tick itself calls, over real FirebaseAdmin
    /// objects built from a real (locally generated) RSA service-account key.
    /// No network is involved: creating a FirebaseApp and resolving its
    /// FirebaseMessaging client are both offline operations, and no message is
    /// ever sent.
    ///
    /// What they do NOT cover: that SendReminderForTask hands its batches to
    /// the returned client. It is private, needs a DbContext and a real FCM
    /// round trip to produce a SendResponse, and is unreachable from this
    /// project — the same gap AdhocCredentialFaultBatchTests documents. The
    /// mitigation is structural: the job has exactly one way to obtain a
    /// FirebaseMessaging, it is the method under test here, and
    /// <see cref="FirebaseMessaging.DefaultInstance"/> appears nowhere in the
    /// production file.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class AdhocFirebaseAppIsolationTests
    {
        private const string AdhocProjectId = "microting-adhoc-test";

        // Stands in for TimePlanning's PushNotificationService: a co-hosted
        // sender that got to FirebaseApp.Create(options) first.
        private const string ForeignProjectId = "microting-timeplanning-test";

        // FirebaseApp instances are process-wide and outlive a test; leaving
        // one behind would make the next Create throw and would leak one
        // test's credential into the next. The default app is reset too
        // because AForeignSenderOwningTheDefaultApp creates one.
        [SetUp]
        public void ResetBefore() => ResetFirebaseApps();

        [TearDown]
        public void ResetAfter() => ResetFirebaseApps();

        private static void ResetFirebaseApps()
        {
            FirebaseApp.GetInstance(AdhocReminderJob.FirebaseAppName)?.Delete();
            FirebaseApp.DefaultInstance?.Delete();
        }

        [Test]
        public void TheAppIsNamed_AndTheProcessWideDefaultIsLeftUntouched()
        {
            // Assert, not Assume: an Assume failure reports as Inconclusive,
            // which would let a leaked app from a previous test silently skip
            // the one test that proves the default app is left alone.
            Assert.That(FirebaseApp.DefaultInstance, Is.Null,
                "precondition: no default app exists at the start of this test");

            var messaging = AdhocReminderJob.EnsureAdhocMessaging(ServiceAccountJson(AdhocProjectId));

            Assert.That(messaging, Is.Not.Null);
            var app = FirebaseApp.GetInstance(AdhocReminderJob.FirebaseAppName);
            Assert.That(app, Is.Not.Null,
                $"the job must own an app named {AdhocReminderJob.FirebaseAppName}");
            Assert.That(app.Name, Is.EqualTo(AdhocReminderJob.FirebaseAppName));

            // The load-bearing assertion: the job created its app WITHOUT
            // claiming the slot every other plugin in the host also reaches
            // for.
            Assert.That(FirebaseApp.DefaultInstance, Is.Null,
                "the job must not create or claim the unnamed default app");
        }

        [Test]
        public void AForeignSenderOwningTheDefaultApp_DoesNotHijackThisSender()
        {
            // TimePlanning (or any co-hosted sender) initialised first and now
            // owns the default app, holding ITS project's credential.
            FirebaseApp.Create(new AppOptions
            {
                Credential = CredentialFactory
                    .FromJson<ServiceAccountCredential>(ServiceAccountJson(ForeignProjectId))
                    .ToGoogleCredential()
            });
            Assert.That(FirebaseApp.DefaultInstance, Is.Not.Null,
                "arrange: the foreign sender must actually own the default app");

            var messaging = AdhocReminderJob.EnsureAdhocMessaging(ServiceAccountJson(AdhocProjectId));

            // Before the named-app fix the job saw a default app, returned
            // early without ever applying its own credential, and then sent
            // through FirebaseMessaging.DefaultInstance — i.e. through
            // TimePlanning's Firebase project.
            Assert.That(FirebaseApp.GetInstance(AdhocReminderJob.FirebaseAppName), Is.Not.Null,
                "an existing default app must not suppress this job's own app");
            Assert.That(messaging, Is.Not.SameAs(FirebaseMessaging.DefaultInstance),
                "sends must not go through the foreign default app's messaging client");
            Assert.That(messaging, Is.SameAs(
                FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(AdhocReminderJob.FirebaseAppName))),
                "sends must go through this job's own named app");
        }

        [Test]
        public void RepeatedTicks_ReuseTheSameApp_AndDoNotThrow()
        {
            var json = ServiceAccountJson(AdhocProjectId);

            var first = AdhocReminderJob.EnsureAdhocMessaging(json);
            // Create throws on a duplicate name, so every tick after the
            // first depends on the guard reading GetInstance(FirebaseAppName)
            // rather than DefaultInstance.
            var second = AdhocReminderJob.EnsureAdhocMessaging(json);

            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void ConcurrentFirstTicks_CreateExactlyOneApp()
        {
            var json = ServiceAccountJson(AdhocProjectId);
            const int racers = 8;

            // Dedicated threads, not the thread pool: the pool injects threads
            // slowly enough that a Barrier over Task.Run would serialise the
            // racers and never exercise the double-checked lock.
            using var startLine = new Barrier(racers);
            var results = new FirebaseMessaging[racers];
            var failures = new Exception[racers];

            var threads = Enumerable.Range(0, racers).Select(i => new Thread(() =>
            {
                startLine.SignalAndWait();
                try
                {
                    results[i] = AdhocReminderJob.EnsureAdhocMessaging(json);
                }
                catch (Exception e)
                {
                    failures[i] = e;
                }
            })).ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.That(failures, Has.All.Null,
                "a loser of the race must not see FirebaseApp.Create reject a duplicate name");
            Assert.That(results, Has.All.SameAs(results[0]),
                "every racer must observe the one app the winner created");
        }

        /// <summary>
        /// A syntactically real service-account key. The private key is
        /// generated here rather than checked in, so nothing in this repo
        /// resembles a leaked credential; FirebaseAdmin parses it eagerly, so
        /// a placeholder string would not do.
        /// </summary>
        private static string ServiceAccountJson(string projectId)
        {
            using var rsa = RSA.Create(2048);
            var privateKey = rsa.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");

            return $$"""
                     {
                       "type": "service_account",
                       "project_id": "{{projectId}}",
                       "private_key_id": "{{Guid.NewGuid():N}}",
                       "private_key": "{{privateKey}}",
                       "client_email": "adhoc@{{projectId}}.iam.gserviceaccount.com",
                       "client_id": "1",
                       "token_uri": "https://oauth2.googleapis.com/token"
                     }
                     """;
        }
    }
}
