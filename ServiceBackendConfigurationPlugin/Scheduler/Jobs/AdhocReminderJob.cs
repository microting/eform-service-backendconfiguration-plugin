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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Sentry;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>
/// Hourly reminder evaluation + FCM push delivery for adhoc tasks.
///
/// Runs on the same hourly <c>_scheduleTimer</c> as <see cref="SearchListJob"/>
/// (see <c>Core.ConfigureScheduler</c>). Feature-flag gated via the
/// <c>BackendConfigurationSettings:AdhocReminderPushEnabled</c>
/// PluginConfigurationValues row (same convention as
/// ComplianceOverdueMovementEnabled) — exits quietly when disabled/unset.
///
/// Due evaluation is delegated to <see cref="AdhocReminderEvaluator"/> in
/// server-local time. Recipients are the SDK Site ids on the task's
/// non-removed <c>AdhocTaskAssignments</c>, widened to all non-removed
/// <c>PropertyWorkers</c> of the task's property when
/// <c>ExecutionRule == 1</c> (everyone). Both of those columns are called
/// <c>WorkerId</c> but hold an SDK <c>Site.Id</c>. FCM tokens come from
/// <c>DeviceTokens</c> (AppId == <see cref="AdhocAppId"/>, WorkflowState ==
/// Created) matched on <c>SdkSiteId</c> — see
/// <see cref="SelectRecipientTokens"/>.
///
/// Idempotency: the task's <c>Last*ReminderSentAt</c> marker is written per
/// <see cref="AdhocReminderEvaluator.ShouldWriteMarker"/> — only after a send
/// attempt with no transient failures, and (for one-shot reminders) only when
/// at least one device was actually delivered to; a one-shot reminder with
/// zero live tokens retries hourly until a token exists. Transient failures
/// always leave the marker unset for retry next hour. Per-token permanent
/// failures (UNREGISTERED / INVALID_ARGUMENT / SENDER_ID_MISMATCH)
/// soft-delete that DeviceToken row and do not by themselves block the
/// marker — EXCEPT a batch in which EVERY response is SENDER_ID_MISMATCH,
/// which is a credential fault rather than dead tokens and prunes nothing;
/// see <see cref="IsCredentialFaultBatch"/>.
///
/// Firebase credentials (service-account JSON) load from the
/// <c>BackendConfigurationSettings:AdhocFirebaseServiceAccountJson</c>
/// PluginConfigurationValues row; when unset the job logs once and no-ops.
/// They are applied to a FirebaseApp named <see cref="FirebaseAppName"/>,
/// never the process-wide default app that co-hosted senders also reach for
/// — see <see cref="EnsureAdhocMessaging"/>.
/// </summary>
public class AdhocReminderJob : IJob
{
    private const string FeatureFlagKey =
        "BackendConfigurationSettings:AdhocReminderPushEnabled";

    private const string ServiceAccountJsonKey =
        "BackendConfigurationSettings:AdhocFirebaseServiceAccountJson";

    // Must match the Android notification channel the mobile app creates.
    private const string AndroidChannelId = "high_importance_channel";

    // FCM rejects SendEach batches above 500 messages.
    private const int FcmBatchLimit = 500;

    /// <summary>
    /// This sender owns the microting-adhoc Firebase project. flutter-adhoc
    /// and flutter-eform register into the SAME DeviceTokens table but mint
    /// their tokens in different Firebase projects, so a token from any
    /// other app would return SenderIdMismatch. Filter them out here rather
    /// than discover it at send time.
    ///
    /// The literal "adhoc" is replicated across four repos and NOTHING
    /// enforces that they agree — drift in any one of them silently orphans
    /// rows while every test stays green:
    ///  - eform-backendconfiguration-base —
    ///    Migrations/20260830081550_DeviceTokenIdentityModel.cs, both the
    ///    backfill (<c>COALESCE(AppId, 'adhoc')</c>) and the column DEFAULT;
    ///  - eform-backendconfiguration-plugin — <c>AdhocAppId</c> in
    ///    BackendConfiguration.Pn.Integration.Test/DeviceTokenRecipientSeamTests.cs,
    ///    and the app_id the SettingsGrpcService push-token endpoint persists;
    ///  - flutter-adhoc — the app_id the client sends when registering;
    ///  - this constant.
    /// </summary>
    public const string AdhocAppId = "adhoc";

    /// <summary>
    /// Name of the FirebaseApp this job owns. Namespaced by vendor and sender
    /// so it cannot collide with another plugin sharing MicrotingService's
    /// load context — see <see cref="EnsureAdhocMessaging"/> for why the
    /// unnamed default app is not an option.
    /// </summary>
    public const string FirebaseAppName = "microting-adhoc";

    // Serialises the first tick's FirebaseApp.Create, which throws on a
    // duplicate name; see EnsureAdhocMessaging.
    private static readonly object FirebaseInitLock = new();

    // "Log once + skip" latch for missing credentials; resets when the key
    // appears so a later removal logs again.
    private static bool _missingCredentialsLogged;

    private readonly BackendConfigurationDbContextHelper _dbContextHelper;

    public AdhocReminderJob(BackendConfigurationDbContextHelper dbContextHelper)
    {
        _dbContextHelper = dbContextHelper;
    }

    public async Task Execute()
    {
        try
        {
            await ExecuteInner();
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: AdhocReminderJob - {e.Message}");
            SentrySdk.CaptureException(e);
        }
    }

    private async Task ExecuteInner()
    {
        await using var db = _dbContextHelper.GetDbContext();

        var featureFlag = await db.PluginConfigurationValues
            .FirstOrDefaultAsync(x => x.Name == FeatureFlagKey);
        if (featureFlag == null || !bool.TryParse(featureFlag.Value, out var isEnabled) || !isEnabled)
        {
            return;
        }

        // Reminder instants are defined in server-local time by design
        // (Europe/Copenhagen deployments).
        var now = DateTime.Now;

        var candidates = await db.AdhocTasks
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => !x.Completed)
            .Where(x => !x.Archived)
            .Where(x => (x.VisibleReminder && x.VisibleFrom != null)
                        || (x.DeadlineReminder && x.Deadline != null))
            .ToListAsync();

        var dueReminders = new List<(AdhocTaskEntity Task, bool IsDeadline)>();
        foreach (var task in candidates)
        {
            if (AdhocReminderEvaluator.DueVisibleReminderInstant(task, now) != null)
            {
                dueReminders.Add((task, false));
            }

            if (AdhocReminderEvaluator.DueDeadlineReminderInstant(task, now) != null)
            {
                dueReminders.Add((task, true));
            }
        }

        if (dueReminders.Count == 0)
        {
            return;
        }

        var serviceAccountJson = (await db.PluginConfigurationValues
            .FirstOrDefaultAsync(x => x.Name == ServiceAccountJsonKey))?.Value;
        if (string.IsNullOrEmpty(serviceAccountJson))
        {
            if (!_missingCredentialsLogged)
            {
                Console.WriteLine(
                    $"warn: AdhocReminderJob - {ServiceAccountJsonKey} is not set; " +
                    $"skipping {dueReminders.Count} due reminder(s)");
                _missingCredentialsLogged = true;
            }

            return;
        }

        _missingCredentialsLogged = false;

        var messaging = EnsureAdhocMessaging(serviceAccountJson);

        foreach (var (task, isDeadline) in dueReminders)
        {
            try
            {
                await SendReminderForTask(db, task, isDeadline, now, messaging);
            }
            catch (Exception e)
            {
                // A failing task leaves its marker unset and retries next
                // hour; it must not sink the remaining due reminders.
                Console.WriteLine($"fail: AdhocReminderJob task {task.Id} - {e.Message}");
                SentrySdk.CaptureException(e);
            }
        }
    }

    /// <summary>
    /// Live adhoc-app device tokens owned by any of <paramref name="sdkSiteIds"/>.
    ///
    /// INVARIANT — the AppId equality predicate must be PRESENT; where it
    /// sits in the chain is irrelevant. <c>AppId</c> is the LEADING column of
    /// <c>IX_DeviceTokens_AppId_SdkSiteId_WorkflowState</c> and the old
    /// <c>IX_DeviceTokens_WorkerId</c> was dropped, so with AppId
    /// unconstrained MariaDB has no usable access path — it has no index skip
    /// scan — and this table-scans. Clause ORDER buys nothing: EF renders the
    /// conjuncts in chain order, but the optimiser normalises the AND and
    /// picks the access path from the predicate SET. Do not read this as a
    /// "put the indexed column first" tuning rule; no such rule exists.
    ///
    /// That index lives in a DIFFERENT repo — eform-backendconfiguration-base,
    /// <c>BackendConfigurationPnDbContext</c> plus
    /// Migrations/20260830081550_DeviceTokenIdentityModel.cs — so this claim
    /// can rot on a base package bump with nothing here to notice. No test in
    /// this repo verifies it; that needs a live MariaDB.
    ///
    /// Public so <c>AdhocReminderRecipientTests</c> can run the shipped
    /// predicate itself instead of a re-stated copy of it.
    /// </summary>
    public static IQueryable<DeviceToken> SelectRecipientTokens(
        IQueryable<DeviceToken> deviceTokens, List<int> sdkSiteIds)
    {
        return deviceTokens
            .Where(x => x.AppId == AdhocAppId)
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created)
            .Where(x => sdkSiteIds.Contains(x.SdkSiteId));
    }

    /// <summary>
    /// True when EVERY message in a send batch failed with
    /// <see cref="MessagingErrorCode.SenderIdMismatch"/>. Each element of
    /// <paramref name="outcomes"/> is one response's error code in batch
    /// order, or <c>null</c> where that message succeeded.
    ///
    /// SenderIdMismatch has TWO causes and only one of them is about tokens:
    ///  (a) a foreign-app token in the shared DeviceTokens table — what
    ///      <see cref="SelectRecipientTokens"/> filters out and what the
    ///      per-token prune is a backstop for; and
    ///  (b) THIS sender holding the wrong credential. If
    ///      <c>BackendConfigurationSettings:AdhocFirebaseServiceAccountJson</c>
    ///      is ever pointed at the wrong Firebase project, every token
    ///      mismatches — and pruning would soft-delete the tenant's ENTIRE
    ///      adhoc DeviceTokens set in a single hourly tick.
    ///
    /// An all-mismatch batch is never the shape (a) predicts, so read it as
    /// (b): prune nothing, count the sends transient so the marker stays
    /// unset, and let the reminder retry next hour once the credential is
    /// fixed. That is the loud-but-harmless behaviour this job had before
    /// pruning existed.
    /// </summary>
    public static bool IsCredentialFaultBatch(IReadOnlyList<MessagingErrorCode?> outcomes)
    {
        return outcomes.Count > 0
               && outcomes.All(x => x == MessagingErrorCode.SenderIdMismatch);
    }

    private static async Task SendReminderForTask(
        BackendConfigurationPnDbContext db, AdhocTaskEntity task, bool isDeadlineReminder, DateTime now,
        FirebaseMessaging messaging)
    {
        // PropertyWorker.WorkerId and AdhocTaskAssignment.WorkerId are both
        // named for the worker but hold an SDK Site.Id — the same value
        // DeviceToken.SdkSiteId holds (see Core.cs, which looks a
        // PropertyWorker up as sdkDbContext.Sites.Single(x => x.Id ==
        // propertyWorker.WorkerId)). Naming the local for the value it
        // carries, not the column it came from, keeps the join below honest.
        List<int> sdkSiteIds;
        if (task.ExecutionRule == 1)
        {
            // 1 = everyone: all workers of the task's property.
            sdkSiteIds = await db.PropertyWorkers
                .Where(x => x.PropertyId == task.PropertyId)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.WorkerId)
                .Distinct()
                .ToListAsync();
        }
        else
        {
            sdkSiteIds = await db.AdhocTaskAssignments
                .Where(x => x.AdhocTaskId == task.Id)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.WorkerId)
                .Distinct()
                .ToListAsync();
        }

        var tokens = sdkSiteIds.Count == 0
            ? new List<DeviceToken>()
            : await SelectRecipientTokens(db.DeviceTokens, sdkSiteIds).ToListAsync();

        var deliveredCount = 0;
        var transientFailures = 0;

        if (tokens.Count > 0)
        {
            var title = string.IsNullOrWhiteSpace(task.Title) ? "Opgave" : task.Title;
            // Due evaluation guarantees the corresponding date is non-null.
            var body = isDeadlineReminder
                ? $"Påmindelse: Opgaven har deadline {task.Deadline!.Value:dd-MM-yyyy}"
                : $"Påmindelse: Opgaven er synlig fra {task.VisibleFrom!.Value:dd-MM-yyyy}";

            var messages = tokens.Select(deviceToken => new Message
            {
                // Message.Token is marked obsolete in FirebaseAdmin 3.6 in
                // favor of Fid, but Fid serializes to the "fid" field
                // (Firebase installation-id targeting) — a DIFFERENT wire
                // field. DeviceTokens stores FCM registration tokens, which
                // the FCM v1 API only accepts via "token".
#pragma warning disable CS0618
                Token = deviceToken.FcmToken,
#pragma warning restore CS0618
                Notification = new Notification
                {
                    Title = title,
                    Body = body
                },
                // The mobile PushRouter routes on exactly this key.
                Data = new Dictionary<string, string>
                {
                    ["taskId"] = task.Id.ToString()
                },
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        ChannelId = AndroidChannelId
                    }
                }
            }).ToList();

            for (var offset = 0; offset < messages.Count; offset += FcmBatchLimit)
            {
                var chunk = messages.Skip(offset).Take(FcmBatchLimit).ToList();
                var batch = await messaging.SendEachAsync(chunk);

                var outcomes = batch.Responses
                    .Select(x => x.IsSuccess ? null : x.Exception?.MessagingErrorCode)
                    .ToList();

                if (IsCredentialFaultBatch(outcomes))
                {
                    var credentialFault =
                        $"AdhocReminderJob - all {outcomes.Count} message(s) in a batch for " +
                        $"task {task.Id} failed with SenderIdMismatch. That is a credential " +
                        $"fault ({ServiceAccountJsonKey} pointing at the wrong Firebase " +
                        $"project), not dead tokens: pruning NOTHING, retrying next hour.";
                    Console.WriteLine($"fail: {credentialFault}");
                    SentrySdk.CaptureMessage(credentialFault, SentryLevel.Error);
                    transientFailures += outcomes.Count;
                    continue;
                }

                for (var i = 0; i < batch.Responses.Count; i++)
                {
                    if (batch.Responses[i].IsSuccess)
                    {
                        deliveredCount++;
                        continue;
                    }

                    var errorCode = outcomes[i];
                    var deviceToken = tokens[offset + i];
                    if (errorCode is MessagingErrorCode.Unregistered
                        or MessagingErrorCode.InvalidArgument)
                    {
                        // Dead token — purge so we stop sending to it.
                        // Removing it is progress, so it does not count
                        // against the batch. Routine churn: info level.
                        Console.WriteLine(
                            $"info: AdhocReminderJob - soft-deleting dead device token " +
                            $"{deviceToken.Id} (site {deviceToken.SdkSiteId}, {errorCode})");
                        await deviceToken.Delete(db);
                    }
                    else if (errorCode is MessagingErrorCode.SenderIdMismatch)
                    {
                        // NOT routine churn — an INVARIANT VIOLATION. The
                        // AppId filter in SelectRecipientTokens is supposed to
                        // make SenderIdMismatch unreachable, so getting here
                        // means a row claims the adhoc AppId while carrying a
                        // token minted in another Firebase project. Still
                        // prune it — counting it transient would leave the
                        // marker unset and retry this reminder hourly forever
                        // — but say so loudly, because the alternative is a
                        // silent delete indistinguishable from UNREGISTERED
                        // churn. A batch where EVERY response mismatches never
                        // reaches here; see IsCredentialFaultBatch.
                        var invariantViolation =
                            $"AdhocReminderJob - SenderIdMismatch on device token " +
                            $"{deviceToken.Id} (site {deviceToken.SdkSiteId}) despite the " +
                            $"AppId == '{AdhocAppId}' filter; soft-deleting it. Check the " +
                            $"row's AppId against the Firebase project that minted it.";
                        Console.WriteLine($"warn: {invariantViolation}");
                        SentrySdk.CaptureMessage(invariantViolation, SentryLevel.Warning);
                        await deviceToken.Delete(db);
                    }
                    else
                    {
                        transientFailures++;
                    }
                }
            }
        }

        var kind = isDeadlineReminder ? "deadline" : "visible";

        if (transientFailures > 0)
        {
            // Leave the marker unset: the whole task+kind retries next
            // hour (healthy tokens may see a duplicate — acceptable).
            Console.WriteLine(
                $"warn: AdhocReminderJob - {transientFailures} transient send failure(s) " +
                $"for {kind} reminder on task {task.Id}; marker left unset for retry next hour");
            return;
        }

        if (!AdhocReminderEvaluator.ShouldWriteMarker(
                isDeadlineReminder, task.DeadlineReminderRepeat, deliveredCount, transientFailures))
        {
            // One-shot reminder with nothing delivered (no registered
            // tokens, or every token was dead and just got purged). Its due
            // instant never recurs, so writing the marker would silently
            // lose the only delivery attempt — leave it unset and retry
            // hourly until a live token exists or the task completes.
            Console.WriteLine(
                $"warn: AdhocReminderJob - 0 live devices for one-shot {kind} reminder " +
                $"on task {task.Id}; marker left unset - will retry next tick");
            return;
        }

        if (isDeadlineReminder)
        {
            task.LastDeadlineReminderSentAt = now;
        }
        else
        {
            task.LastVisibleReminderSentAt = now;
        }

        await task.Update(db);

        if (deliveredCount > 0)
        {
            Console.WriteLine(
                $"info: AdhocReminderJob - sent {kind} reminder for task {task.Id} " +
                $"to {deliveredCount} device(s)");
        }
        else
        {
            // Weekday-repeat with nobody reachable: today's slot is marked
            // done (NOT delivered) — the reminder self-heals next weekday.
            Console.WriteLine(
                $"warn: AdhocReminderJob - 0 recipients for weekday-repeat deadline reminder " +
                $"on task {task.Id}; today's slot marked done, next attempt next weekday");
        }
    }

    /// <summary>
    /// The FirebaseMessaging client this job sends through, creating the job's
    /// own <see cref="FirebaseApp"/> on the first tick.
    ///
    /// The app is NAMED, and must stay named. <c>FirebaseApp.DefaultInstance</c>
    /// is a PROCESS-WIDE singleton, and MicrotingService loads every service
    /// plugin into one shared load context (the Google.Apis version pin in
    /// ServiceBackendConfigurationPlugin.csproj is the other consequence of
    /// that). Microting already runs a second FCM sender on the same
    /// FirebaseAdmin API — TimePlanning's PushNotificationService — with a
    /// third (flutter-eform) on the way, and each authenticates to a DIFFERENT
    /// Firebase project. Two senders both creating the default app means
    /// whichever initialises FIRST wins, and every later one then silently
    /// pushes through the winner's project and credential.
    ///
    /// Nothing surfaces that. Cross-project sends fail per token with
    /// SenderIdMismatch, <see cref="IsCredentialFaultBatch"/> reads a whole
    /// batch of those as a credential fault — correctly, from what it can see
    /// — so no token is pruned, no exception escapes, and the job retries
    /// hourly forever. Do not "simplify" this back to DefaultInstance.
    ///
    /// Public so AdhocFirebaseAppIsolationTests can assert on the very client
    /// the hourly tick sends through, instead of a re-stated copy of it.
    /// </summary>
    public static FirebaseMessaging EnsureAdhocMessaging(string serviceAccountJson)
    {
        var app = FirebaseApp.GetInstance(FirebaseAppName);
        if (app == null)
        {
            lock (FirebaseInitLock)
            {
                // Re-read inside the lock. FirebaseApp.Create THROWS
                // ArgumentException when an app of this name already exists,
                // so a racing first tick must observe the winner's app rather
                // than attempt a second Create; GetInstance returns null when
                // the app is absent, which is what makes that check possible.
                app = FirebaseApp.GetInstance(FirebaseAppName) ?? FirebaseApp.Create(
                    new AppOptions
                    {
                        // CredentialFactory is the non-obsolete replacement for
                        // GoogleCredential.FromJson; pinning the generic to
                        // ServiceAccountCredential also fails fast (into the job's
                        // try/catch + Sentry) if the configured JSON is not a
                        // service-account key.
                        Credential = CredentialFactory
                            .FromJson<ServiceAccountCredential>(serviceAccountJson)
                            .ToGoogleCredential()
                    },
                    FirebaseAppName);
            }
        }

        return FirebaseMessaging.GetMessaging(app);
    }
}
