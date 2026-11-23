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
namespace ServiceBackendConfigurationPlugin.Integration.Test.TaskReminders;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

[TestFixture]
public class TaskReminderDelivererTests : TaskReminderTestBase
{
#pragma warning disable CS0618 // Message.Token: the FCM registration token field (see TaskReminderMessages)
    private static string TokenOf(Message message) => message.Token;
#pragma warning restore CS0618

    private static string Epoch(DateTime utc) =>
        new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<TaskReminderSendOutcome> All(IReadOnlyList<Message> messages, MessagingErrorCode code) =>
        messages.Select(_ => new TaskReminderSendOutcome(false, code)).ToList();

    [Test]
    public async Task DueRow_IsSentToTheSitesOptedInEformTokensOnly_WithTheExactPayload_AndMarkedSent()
    {
        await TokenAsync(31, "opted-in");
        await TokenAsync(31, "opted-out", taskReminders: false);
        await TokenAsync(31, "adhoc-app", appId: "adhoc");
        await TokenAsync(31, "removed", removed: true);
        await TokenAsync(31, "");
        await TokenAsync(32, "other-site");
        var row = await PlanAsync(startsIn: TimeSpan.FromMinutes(10), complianceId: 900);

        var result = await RunAsync();

        Assert.That(result.Sent, Is.EqualTo(1));
        var message = Sender.AllMessages().Single();
        Assert.Multiple(() =>
        {
            Assert.That(TokenOf(message), Is.EqualTo("opted-in"));
            Assert.That(message.Notification.Title, Is.EqualTo("Starter kl. 08:00"));
            Assert.That(message.Notification.Body, Is.EqualTo("Fodring af kalve · Gården"));
            Assert.That(message.Data, Is.EquivalentTo(new Dictionary<string, string>
            {
                ["type"] = "task_due",
                ["v"] = "1",
                ["event_id"] = "7",
                ["plan_day_key"] = "2026-11-23",
                ["property_id"] = "42",
                ["compliance_id"] = "900",
                ["planned_start_utc"] = "2026-11-23T07:00:00Z",
                ["tag"] = "task:7:2026-11-23:20261123T0700Z"
            }));
            Assert.That(message.Android.Priority, Is.EqualTo(Priority.High));
            Assert.That(message.Android.TimeToLive, Is.EqualTo(TimeSpan.FromMinutes(10)), "expires at the planned start");
            Assert.That(message.Android.Notification.ChannelId, Is.EqualTo("tasks"));
            Assert.That(message.Android.Notification.Tag, Is.EqualTo("task:7:2026-11-23:20261123T0700Z"));
            Assert.That(message.Apns.Headers, Is.EquivalentTo(new Dictionary<string, string>
            {
                ["apns-collapse-id"] = "task:7:2026-11-23:20261123T0700Z",
                ["apns-expiration"] = Epoch(new DateTime(2026, 11, 23, 7, 0, 0, DateTimeKind.Utc))
            }));
            Assert.That(message.Apns.Aps.ThreadId, Is.EqualTo("tasks"));
            Assert.That(message.Apns.Aps.ContentAvailable, Is.False);
        });

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(after.SentAtUtc, Is.EqualTo(Now));
    }

    [Test]
    public async Task DueRow_WithoutCompliance_OmitsComplianceId()
    {
        await TokenAsync(31, "t");
        await PlanAsync(complianceId: null);

        await RunAsync();

        Assert.That(Sender.AllMessages().Single().Data.ContainsKey("compliance_id"), Is.False);
    }

    [Test]
    public async Task RowPastItsStart_IsExpired_NotSent()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync(startsIn: TimeSpan.Zero);

        await RunAsync();

        Assert.That(Sender.Calls, Is.Empty);
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Expired));
    }

    [Test]
    public async Task NoOptedInToken_IsMarkedSent_WithoutACall()
    {
        await TokenAsync(31, "opted-out", taskReminders: false);
        var row = await PlanAsync();

        await RunAsync();

        Assert.That(Sender.Calls, Is.Empty);
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
    }

    [Test]
    public async Task TransientFailure_RetriesWithBackOff_AndExpiresRatherThanRetryAtOrAfterTheStart()
    {
        await TokenAsync(31, "t");
        Sender.Respond = m => All(m, MessagingErrorCode.Unavailable);
        var row = await PlanAsync(startsIn: TimeSpan.FromSeconds(7));

        await RunAsync();
        var first = await RowAsync(row.Id);
        Assert.Multiple(() =>
        {
            Assert.That(first.State, Is.EqualTo(TaskNotificationState.Pending));
            Assert.That(first.Attempts, Is.EqualTo(1));
            Assert.That(first.LastError, Is.EqualTo("unavailable"));
            Assert.That(first.NextAttemptAtUtc, Is.EqualTo(Now.AddSeconds(2)), "first back-off 2 s");
        });

        await RunAsync();
        Assert.That(Sender.Calls, Has.Count.EqualTo(1), "not due again before its back-off");

        Now = Now.AddSeconds(2);
        await RunAsync();
        var second = await RowAsync(row.Id);
        Assert.That(second.NextAttemptAtUtc, Is.EqualTo(Now.AddSeconds(4)), "second back-off 4 s, still before the start");

        Now = Now.AddSeconds(4); // 1 s before the start; the next back-off (8 s) would pass it
        await RunAsync();
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Expired));
        Assert.That(Sender.Calls, Has.Count.EqualTo(3));
    }

    [Test]
    public void BackOff_Is2_4_8_16_32_ThenCappedAt60Seconds()
    {
        Assert.That(Enumerable.Range(1, 8).Select(a => TaskReminderDeliverer.BackOff(a).TotalSeconds),
            Is.EqualTo(new double[] { 2, 4, 8, 16, 32, 60, 60, 60 }));
    }

    [Test]
    public async Task WholeCallThrows_IsTransient()
    {
        await TokenAsync(31, "t");
        Sender.BeforeSend = _ => throw new HttpRequestExceptionStandIn();
        var row = await PlanAsync();

        await RunAsync();

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Pending));
        Assert.That(after.LastError, Is.EqualTo("send-error"));
    }

    private sealed class HttpRequestExceptionStandIn : Exception;

    [Test]
    public async Task PermanentFailure_IsFailedWithACode_TokenKept()
    {
        var token = await TokenAsync(31, "ios-token");
        Sender.Respond = m => All(m, MessagingErrorCode.ThirdPartyAuthError);
        var row = await PlanAsync();

        await RunAsync();

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Failed));
        Assert.That(after.LastError, Is.EqualTo("third-party-auth-error"));
        Assert.That(await TokenStateAsync(token.Id), Is.Not.EqualTo(Removed));
    }

    [Test]
    public async Task DeadToken_IsSoftDeleted_WhileTheOtherTokenGetsTheReminder()
    {
        var dead = await TokenAsync(31, "dead");
        var live = await TokenAsync(31, "live");
        Sender.Respond = m => m.Select(x => TokenOf(x) == "dead"
            ? new TaskReminderSendOutcome(false, MessagingErrorCode.Unregistered)
            : new TaskReminderSendOutcome(true, null)).ToList();
        var row = await PlanAsync();

        await RunAsync();

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(await TokenStateAsync(dead.Id), Is.EqualTo(Removed));
        Assert.That(await TokenStateAsync(live.Id), Is.Not.EqualTo(Removed));
    }

    [Test]
    public async Task EveryTokenSenderIdMismatch_IsACredentialFault_NothingPruned_Retried()
    {
        var a = await TokenAsync(31, "a");
        var b = await TokenAsync(31, "b");
        Sender.Respond = m => All(m, MessagingErrorCode.SenderIdMismatch);
        var row = await PlanAsync();

        await RunAsync();

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Pending));
        Assert.That(after.LastError, Is.EqualTo("sender-id-mismatch"));
        Assert.That(await TokenStateAsync(a.Id), Is.Not.EqualTo(Removed));
        Assert.That(await TokenStateAsync(b.Id), Is.Not.EqualTo(Removed));
    }

    [Test]
    public async Task EveryTokenInvalidArgument_IsAPayloadFault_NothingPruned_Failed()
    {
        var a = await TokenAsync(31, "a");
        var b = await TokenAsync(31, "b");
        Sender.Respond = m => All(m, MessagingErrorCode.InvalidArgument);
        var row = await PlanAsync();

        await RunAsync();

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Failed));
        Assert.That(after.LastError, Is.EqualTo("invalid-argument"));
        Assert.That(await TokenStateAsync(a.Id), Is.Not.EqualTo(Removed));
        Assert.That(await TokenStateAsync(b.Id), Is.Not.EqualTo(Removed));
    }

    /// <summary>
    /// InvalidArgument is read as a fault in OUR message (e.g. APNs headers Apple rejects), never as a
    /// dead token: on a mixed site the iOS tokens must not be soft-deleted while Android accepts.
    /// </summary>
    [Test]
    public async Task MixedSite_IosTokenInvalidArgument_AndroidAccepts_RowSent_NoTokenRemoved()
    {
        var ios = await TokenAsync(31, "ios");
        var android = await TokenAsync(31, "android");
        Sender.Respond = m => m.Select(x => TokenOf(x) == "ios"
            ? new TaskReminderSendOutcome(false, MessagingErrorCode.InvalidArgument)
            : new TaskReminderSendOutcome(true, null)).ToList();
        var row = await PlanAsync();

        await RunAsync();

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(await TokenStateAsync(ios.Id), Is.Not.EqualTo(Removed), "an invalid-argument answer never removes a token");
        Assert.That(await TokenStateAsync(android.Id), Is.Not.EqualTo(Removed));
    }

    [Test]
    public async Task SingleTokenInvalidArgument_RowFailed_TokenKept()
    {
        var token = await TokenAsync(31, "only");
        Sender.Respond = m => All(m, MessagingErrorCode.InvalidArgument);
        var row = await PlanAsync();

        await RunAsync();

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Failed));
        Assert.That(after.LastError, Is.EqualTo("invalid-argument"));
        Assert.That(await TokenStateAsync(token.Id), Is.Not.EqualTo(Removed));
    }

    /// <summary>
    /// Deliberate exception to the opt-in: a withdraw goes to every live eForm token of the site, so a
    /// phone that showed a reminder and then switched reminders off still has it removed. A reminder
    /// keeps the opt-in filter.
    /// </summary>
    [Test]
    public async Task OptedOutToken_GetsTheWithdraw_ButNeverAReminder()
    {
        await TokenAsync(31, "opted-out", taskReminders: false);
        await TokenAsync(31, "other-app", appId: "adhoc", taskReminders: false);
        await TokenAsync(31, "removed", taskReminders: false, removed: true);
        var row = await PlanAsync();

        await RunAsync();
        Assert.That(Sender.Calls, Is.Empty, "no reminder to an opted-out token");

        await using (var db = DbHelper.GetDbContext())
        {
            await TaskNotificationOutbox.CancelOrWithdrawAsync(db, [row.Id], Now);
        }

        await RunAsync();

        var message = Sender.AllMessages().Single();
        Assert.That(TokenOf(message), Is.EqualTo("opted-out"));
        Assert.That(message.Data["type"], Is.EqualTo("task_withdrawn"));
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Withdrawn));
    }

    /// <summary>
    /// Documented limit: the row is Sent as soon as one phone accepted; a phone that failed transiently
    /// in the same call is not retried (one row per reminder, not per phone).
    /// </summary>
    [Test]
    public async Task PartialDelivery_OneTransientFailure_RowSent_OtherPhoneNotRetried()
    {
        await TokenAsync(31, "ok");
        await TokenAsync(31, "flaky");
        Sender.Respond = m => m.Select(x => TokenOf(x) == "flaky"
            ? new TaskReminderSendOutcome(false, MessagingErrorCode.Unavailable)
            : new TaskReminderSendOutcome(true, null)).ToList();
        var row = await PlanAsync();

        await RunAsync();
        Now = Now.AddMinutes(1);
        await RunAsync();

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(Sender.Calls, Has.Count.EqualTo(1), "the flaky phone is not retried");
    }

    /// <summary>
    /// The sender accepted, then the tick was cancelled before the row was marked: the row stays in
    /// Sending until its lease expires, then it is re-sent once, with the same tag (the phone replaces
    /// the first copy).
    /// </summary>
    [Test]
    public async Task TickCancelledAfterTheSenderAccepted_RowStaysSendingUntilTheLease_ThenIsResentOnceWithTheSameTag()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync(startsIn: TimeSpan.FromMinutes(10));
        using var tick = new CancellationTokenSource();
        Sender.BeforeSend = _ =>
        {
            tick.Cancel(); // e.g. the service stops right after FCM accepted
            return Task.CompletedTask;
        };

        Assert.That(async () => await Deliverer().RunOnceAsync(TimeSpan.FromSeconds(30), tick.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sending));

        Sender.BeforeSend = _ => Task.CompletedTask;
        await RunAsync();
        Assert.That(Sender.Calls, Has.Count.EqualTo(1), "not re-sent while the lease runs");

        Now = Now + TaskReminderDeliverer.Lease + TimeSpan.FromSeconds(1);
        var deliverer = Deliverer();
        await deliverer.ReapAsync(CancellationToken.None);
        await RunAsync(deliverer);
        await RunAsync(deliverer);

        var tags = Sender.AllMessages().Select(x => x.Data["tag"]).ToList();
        Assert.That(tags, Is.EqualTo(new[] { "task:7:2026-11-23:20261123T0700Z", "task:7:2026-11-23:20261123T0700Z" }), "sent twice in all, same tag");
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
    }

    /// <summary>A send that started before the start and returned after it: the row is still Sent, and
    /// never sent again.</summary>
    [Test]
    public async Task SendReturnsAfterTheStart_RowIsSent_AndNeverResent()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync(startsIn: TimeSpan.FromSeconds(5));
        Sender.BeforeSend = _ =>
        {
            Now = row.PlannedStartUtc.AddSeconds(3);
            return Task.CompletedTask;
        };

        await RunAsync();
        Sender.BeforeSend = _ => Task.CompletedTask;
        Now = Now.AddMinutes(5);
        await RunAsync();

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(Sender.Calls, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A key for the wrong Firebase project: every real token mismatches, and one other token answers
    /// something else. Without a success in the batch nothing proves the credential, so no token is
    /// pruned and the row is retried.
    /// </summary>
    [Test]
    public async Task WrongProjectBatch_MismatchesPlusOneUnavailable_NothingPruned_RowRetried()
    {
        var a = await TokenAsync(31, "a");
        var b = await TokenAsync(31, "b");
        var c = await TokenAsync(31, "flaky");
        Sender.Respond = m => m.Select(x => TokenOf(x) == "flaky"
            ? new TaskReminderSendOutcome(false, MessagingErrorCode.Unavailable)
            : new TaskReminderSendOutcome(false, MessagingErrorCode.SenderIdMismatch)).ToList();
        var row = await PlanAsync();

        await RunAsync();

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Pending));
        Assert.That(after.LastError, Is.EqualTo("sender-id-mismatch"));
        foreach (var token in new[] { a, b, c })
        {
            Assert.That(await TokenStateAsync(token.Id), Is.Not.EqualTo(Removed));
        }
    }

    [Test]
    public async Task WrongProjectBatches_LogOneWarning_NotOnePerReminderAndRetry()
    {
        await TokenAsync(31, "a");
        Sender.Respond = m => All(m, MessagingErrorCode.SenderIdMismatch);
        await PlanAsync(eventId: 1, startsIn: TimeSpan.FromMinutes(30));
        await PlanAsync(eventId: 2, startsIn: TimeSpan.FromMinutes(30));

        var deliverer = Deliverer();
        await RunAsync(deliverer);
        Now = Now.AddMinutes(1);
        await RunAsync(deliverer);

        Assert.That(Sender.Calls, Has.Count.EqualTo(4), "two reminders, each tried twice");
        Assert.That(Logs.Count(x => x.Contains("sender-id-mismatch and none succeeded")), Is.EqualTo(1));
    }

    [Test]
    public async Task WrongProjectBatch_MismatchesPlusOneUnregistered_OnlyTheUnregisteredTokenPruned_RowRetried()
    {
        var a = await TokenAsync(31, "a");
        var dead = await TokenAsync(31, "dead");
        Sender.Respond = m => m.Select(x => TokenOf(x) == "dead"
            ? new TaskReminderSendOutcome(false, MessagingErrorCode.Unregistered)
            : new TaskReminderSendOutcome(false, MessagingErrorCode.SenderIdMismatch)).ToList();
        var row = await PlanAsync();

        await RunAsync();

        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Pending));
        Assert.That(after.LastError, Is.EqualTo("sender-id-mismatch"));
        Assert.That(await TokenStateAsync(a.Id), Is.Not.EqualTo(Removed));
        Assert.That(await TokenStateAsync(dead.Id), Is.EqualTo(Removed));
    }


    // ---------------------------------------------------------------- PR #612 review

    [Test]
    public async Task Withdraw_OnePhoneAcceptsOneTransient_StaysRetryable_ThenWithdrawn()
    {
        await TokenAsync(31, "ok");
        await TokenAsync(31, "flaky");
        var row = await PlanAsync();
        await RunAsync();
        await using (var db = DbHelper.GetDbContext())
        {
            await TaskNotificationOutbox.CancelOrWithdrawAsync(db, [row.Id], Now);
        }

        Sender.Respond = m => m.Select(x => TokenOf(x) == "flaky"
            ? new TaskReminderSendOutcome(false, MessagingErrorCode.Unavailable)
            : new TaskReminderSendOutcome(true, null)).ToList();
        await RunAsync();
        var afterFirst = await RowAsync(row.Id);
        Assert.That(afterFirst.State, Is.EqualTo(TaskNotificationState.WithdrawPending),
            "a phone that failed transiently keeps the withdrawal retryable");
        Assert.That(afterFirst.LastError, Is.EqualTo("unavailable"));

        Sender.Respond = m => m.Select(_ => new TaskReminderSendOutcome(true, null)).ToList();
        Now = afterFirst.NextAttemptAtUtc;
        await RunAsync();
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Withdrawn));
    }

    [Test]
    public async Task RescheduledTask_OldAndNewRowsHaveDifferentTags_InBothMessageKinds()
    {
        await TokenAsync(31, "t");
        var first = await PlanAsync(startsIn: TimeSpan.FromMinutes(10));
        var moved = await PlanAsync(startsIn: TimeSpan.FromMinutes(40));
        await RunAsync();
        await using (var db = DbHelper.GetDbContext())
        {
            await TaskNotificationOutbox.CancelOrWithdrawAsync(db, [first.Id], Now);
        }

        await RunAsync();

        var messages = Sender.AllMessages();
        var withdraw = messages.Single(x => x.Data["type"] == "task_withdrawn");
        var replacement = messages.Single(x => x.Data["type"] == "task_due" && x.Data["planned_start_utc"] == "2026-11-23T07:30:00Z");
        Assert.That(withdraw.Data["tag"], Is.EqualTo("task:7:2026-11-23:20261123T0700Z"));
        Assert.That(replacement.Data["tag"], Is.EqualTo("task:7:2026-11-23:20261123T0730Z"));
        Assert.That(replacement.Android.Notification.Tag, Is.EqualTo(replacement.Data["tag"]));
        Assert.That(replacement.Apns.Headers["apns-collapse-id"], Is.EqualTo(replacement.Data["tag"]));
        Assert.That(moved.Id, Is.Not.EqualTo(first.Id));
    }

    [Test]
    public void Tag_WorstCase_FitsTheApnsCollapseIdLimitOf64Bytes()
    {
        var row = new Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.TaskNotification
        {
            AreaRulePlanningId = int.MaxValue, PlanDayKey = "9999-12-31",
            PlannedStartUtc = new DateTime(9999, 12, 31, 23, 59, 0, DateTimeKind.Utc)
        };

        var tag = TaskReminderMessages.Tag(row);

        Assert.That(tag, Is.EqualTo("task:2147483647:9999-12-31:99991231T2359Z"));
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(tag), Is.LessThanOrEqualTo(64));
    }

    /// <summary>The recipient query took so long that the start passed: nothing is sent, the row expires.</summary>
    [Test]
    public async Task StartPassesBeforeTheFirstFirebaseCall_NothingSent_RowExpired()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync(startsIn: TimeSpan.FromSeconds(5));
        // Clock reads: the claim, the check before the recipient query, then the check right before the
        // Firebase call, which already sees the start passed (a slow query).
        var reads = 0;
        var deliverer = new TaskReminderDeliverer(Contexts.CreateAsync, Sender,
            () => ++reads <= 2 ? Now : row.PlannedStartUtc.AddSeconds(1), Logs.Add);

        await deliverer.RunOnceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.That(Sender.Calls, Is.Empty);
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Expired));
    }

    /// <summary>More than 500 tokens: the first call crossed the start, so the second chunk is not sent and
    /// the row is Sent (some phones accepted).</summary>
    [Test]
    public async Task StartPassesBetweenChunks_RemainingChunksNotSent_RowSent()
    {
        await using (var db = DbHelper.GetDbContext())
        {
            for (var i = 0; i < TaskReminderDeliverer.FcmBatchLimit + 1; i++)
            {
                db.DeviceTokens.Add(new Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.DeviceToken
                {
                    AppId = TaskReminderDeliverer.EformAppId, InstallationId = $"bulk-{i}", FcmToken = $"bulk-{i}",
                    SdkSiteId = 31, Platform = "android", TaskReminders = true,
                    WorkflowState = Microting.eForm.Infrastructure.Constants.Constants.WorkflowStates.Created,
                    CreatedAt = Now, UpdatedAt = Now, Version = 1
                });
            }

            await db.SaveChangesAsync();
        }

        var row = await PlanAsync(startsIn: TimeSpan.FromSeconds(5));
        Sender.BeforeSend = _ =>
        {
            Now = row.PlannedStartUtc.AddSeconds(1);
            return Task.CompletedTask;
        };

        await RunAsync();

        Assert.That(Sender.Calls, Has.Count.EqualTo(1), "the second chunk would start after the start");
        Assert.That(Sender.Calls[0], Has.Count.EqualTo(TaskReminderDeliverer.FcmBatchLimit));
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
    }

    /// <summary>One prune fails (the token row vanished meanwhile); the next dead token's removal must still
    /// be saved.</summary>
    [Test]
    public async Task OnePruneFails_TheNextDeadTokenIsStillRemoved()
    {
        var vanishing = await TokenAsync(31, "vanishing");
        var dead = await TokenAsync(31, "dead");
        await TokenAsync(31, "live");
        Sender.Respond = m => m.Select(x => TokenOf(x) == "live"
            ? new TaskReminderSendOutcome(true, null)
            : new TaskReminderSendOutcome(false, MessagingErrorCode.Unregistered)).ToList();
        Sender.BeforeSend = async _ =>
        {
            await using var db = DbHelper.GetDbContext();
            // Test data in a private database: the row disappears under the deliverer, so its prune fails.
            await db.DeviceTokens.Where(x => x.Id == vanishing.Id).ExecuteDeleteAsync();
        };
        var row = await PlanAsync();

        await RunAsync();

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(await TokenStateAsync(dead.Id), Is.EqualTo(Removed), "the removal after a failed one is persisted");
    }

    [Test]
    public async Task Expiry_IsTheRemainingTimeBeforeTheStart_AtLeastOneSecond()
    {
        await TokenAsync(31, "t");
        await PlanAsync(eventId: 1, startsIn: TimeSpan.FromMinutes(3));

        await RunAsync();

        Assert.That(Sender.AllMessages().Single().Android.TimeToLive, Is.EqualTo(TimeSpan.FromMinutes(3)));
        var row = new Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.TaskNotification
        {
            AreaRulePlanningId = 1, PlanDayKey = "2026-11-23", PlannedStartUtc = Now, Title = "t", Body = "b"
        };
        Assert.That(TaskReminderMessages.TaskDue(row, "t", Now.AddMilliseconds(-200)).Android.TimeToLive,
            Is.EqualTo(TimeSpan.FromSeconds(1)), "never zero or negative");
    }

    [Test]
    public async Task OneOfTwoSenderIdMismatch_ThatTokenIsPruned()
    {
        var foreign = await TokenAsync(31, "foreign");
        await TokenAsync(31, "ok");
        Sender.Respond = m => m.Select(x => TokenOf(x) == "foreign"
            ? new TaskReminderSendOutcome(false, MessagingErrorCode.SenderIdMismatch)
            : new TaskReminderSendOutcome(true, null)).ToList();
        var row = await PlanAsync();

        await RunAsync();

        Assert.That(await TokenStateAsync(foreign.Id), Is.EqualTo(Removed), "a success in the batch proves the credential");
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
    }

    [Test]
    public async Task Withdraw_SendsASilentMessage_AndIsMarkedWithdrawn()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync();
        await RunAsync(); // sent
        await using (var db = DbHelper.GetDbContext())
        {
            await TaskNotificationOutbox.CancelOrWithdrawAsync(db, [row.Id], Now);
        }

        Sender.Calls.Clear();
        var result = await RunAsync();

        Assert.That(result.Withdrawn, Is.EqualTo(1));
        var message = Sender.AllMessages().Single();
        Assert.Multiple(() =>
        {
            Assert.That(TokenOf(message), Is.EqualTo("t"));
            Assert.That(message.Notification, Is.Null, "silent: nothing is shown");
            Assert.That(message.Data, Is.EquivalentTo(new Dictionary<string, string>
            {
                ["type"] = "task_withdrawn",
                ["v"] = "1",
                ["event_id"] = "7",
                ["plan_day_key"] = "2026-11-23",
                ["planned_start_utc"] = "2026-11-23T07:00:00Z",
                ["tag"] = "task:7:2026-11-23:20261123T0700Z"
            }));
            Assert.That(message.Android.Priority, Is.EqualTo(Priority.High));
            Assert.That(message.Android.TimeToLive, Is.EqualTo(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(10)),
                "expires 24 h after the start, the retry cut-off");
            Assert.That(message.Android.Notification, Is.Null);
            Assert.That(message.Apns.Aps.ContentAvailable, Is.True);
            Assert.That(message.Apns.Headers, Is.EquivalentTo(new Dictionary<string, string>
            {
                ["apns-push-type"] = "background",
                ["apns-priority"] = "5",
                ["apns-expiration"] = Epoch(new DateTime(2026, 11, 24, 7, 0, 0, DateTimeKind.Utc))
            }));
        });
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Withdrawn));
    }

    [Test]
    public async Task Withdraw_TransientFailure_RetriesUntil24HoursAfterTheStart_ThenFails()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync();
        await RunAsync();
        await using (var db = DbHelper.GetDbContext())
        {
            await TaskNotificationOutbox.CancelOrWithdrawAsync(db, [row.Id], Now);
        }

        Sender.Respond = m => All(m, MessagingErrorCode.Unavailable);
        await RunAsync();
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.WithdrawPending));

        Now = row.PlannedStartUtc + TimeSpan.FromHours(24) - TimeSpan.FromSeconds(5);
        await RunAsync();
        var after = await RowAsync(row.Id);
        Assert.That(after.State, Is.EqualTo(TaskNotificationState.Failed), "the next retry (8 s after the third attempt) would be past the window");
        Assert.That(after.LastError, Is.EqualTo("withdraw-too-late"));
    }

    [Test]
    public async Task TwoDeliverersOverTheSameRows_SendEachRowOnce()
    {
        await TokenAsync(31, "t");
        var ids = new List<long>();
        for (var i = 1; i <= 20; i++)
        {
            ids.Add((await PlanAsync(eventId: 100 + i)).Id);
        }

        var otherSender = new FakeTaskReminderSender();
        var results = await Task.WhenAll(RunAsync(Deliverer()), RunAsync(Deliverer(otherSender)));

        var sentEvents = Sender.AllMessages().Concat(otherSender.AllMessages()).Select(x => x.Data["event_id"]).ToList();
        Assert.That(sentEvents, Has.Count.EqualTo(20));
        Assert.That(sentEvents, Is.Unique);
        Assert.That(results.Sum(x => x.Sent), Is.EqualTo(20));
        foreach (var id in ids)
        {
            Assert.That((await RowAsync(id)).State, Is.EqualTo(TaskNotificationState.Sent));
        }
    }

    [Test]
    public async Task ReapedLease_IsSentByAnotherDeliverer_AndTheLateFinishersMarkIsRefused()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync(startsIn: TimeSpan.FromMinutes(10));
        // Deliverer A claims and then hangs.
        Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.TaskNotification claimedByA;
        await using (var db = DbHelper.GetDbContext())
        {
            claimedByA = (await TaskNotificationOutbox.ClaimDueAsync(db, Now, 50, TaskReminderDeliverer.Lease)).Single();
        }

        Now = Now + TaskReminderDeliverer.Lease + TimeSpan.FromSeconds(1);
        var b = Deliverer();
        Assert.That(await b.ReapAsync(CancellationToken.None), Is.EqualTo(1));
        await RunAsync(b);

        Assert.That(Sender.AllMessages(), Has.Count.EqualTo(1), "B sent it");
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        await using (var db = DbHelper.GetDbContext())
        {
            Assert.That(await TaskNotificationOutbox.MarkSentAsync(db, claimedByA, Now), Is.False,
                "A's claim is stale: its mark is refused");
            Assert.That(await TaskNotificationOutbox.MarkRetryAsync(db, claimedByA, Now.AddSeconds(2), "unavailable", Now), Is.False);
        }

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
    }

    [Test]
    public async Task RowsLeftWhenThePassBudgetIsSpent_AreHandedBackAtOnce()
    {
        await TokenAsync(31, "t");
        var first = await PlanAsync(eventId: 1);
        var second = await PlanAsync(eventId: 2);

        var result = await Deliverer().RunOnceAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.That(result.Deferred, Is.EqualTo(2));
        Assert.That(Sender.Calls, Is.Empty);
        foreach (var id in new[] { first.Id, second.Id })
        {
            var row = await RowAsync(id);
            Assert.That(row.State, Is.EqualTo(TaskNotificationState.Pending));
            Assert.That(row.NextAttemptAtUtc, Is.EqualTo(Now), "due again on the next pass, not after the lease");
        }
    }
}
