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
/// server-local time. Recipients are the task's non-removed
/// <c>AdhocTaskAssignments</c> worker ids, widened to all non-removed
/// <c>PropertyWorkers</c> of the task's property when
/// <c>ExecutionRule == 1</c> (everyone). FCM tokens come from
/// <c>DeviceTokens</c> (WorkflowState == Created).
///
/// Idempotency: the task's <c>Last*ReminderSentAt</c> marker is written per
/// <see cref="AdhocReminderEvaluator.ShouldWriteMarker"/> — only after a send
/// attempt with no transient failures, and (for one-shot reminders) only when
/// at least one device was actually delivered to; a one-shot reminder with
/// zero live tokens retries hourly until a token exists. Transient failures
/// always leave the marker unset for retry next hour. Per-token permanent
/// failures (UNREGISTERED / INVALID_ARGUMENT) soft-delete that DeviceToken
/// row and do not by themselves block the marker.
///
/// Firebase credentials (service-account JSON) load from the
/// <c>BackendConfigurationSettings:AdhocFirebaseServiceAccountJson</c>
/// PluginConfigurationValues row; when unset the job logs once and no-ops.
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

    // FirebaseApp.Create throws on double-init; the guard makes the hourly
    // ticks (and any future co-hosted sender) initialize exactly once.
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

        EnsureFirebaseApp(serviceAccountJson);

        foreach (var (task, isDeadline) in dueReminders)
        {
            try
            {
                await SendReminderForTask(db, task, isDeadline, now);
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

    private static async Task SendReminderForTask(
        BackendConfigurationPnDbContext db, AdhocTaskEntity task, bool isDeadlineReminder, DateTime now)
    {
        List<int> workerIds;
        if (task.ExecutionRule == 1)
        {
            // 1 = everyone: all workers of the task's property.
            workerIds = await db.PropertyWorkers
                .Where(x => x.PropertyId == task.PropertyId)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.WorkerId)
                .Distinct()
                .ToListAsync();
        }
        else
        {
            workerIds = await db.AdhocTaskAssignments
                .Where(x => x.AdhocTaskId == task.Id)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.WorkerId)
                .Distinct()
                .ToListAsync();
        }

        var tokens = workerIds.Count == 0
            ? new List<DeviceToken>()
            : await db.DeviceTokens
                .Where(x => x.WorkflowState == Constants.WorkflowStates.Created)
                .Where(x => workerIds.Contains(x.WorkerId))
                .ToListAsync();

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
                var batch = await FirebaseMessaging.DefaultInstance.SendEachAsync(chunk);

                for (var i = 0; i < batch.Responses.Count; i++)
                {
                    var response = batch.Responses[i];
                    if (response.IsSuccess)
                    {
                        deliveredCount++;
                        continue;
                    }

                    var deviceToken = tokens[offset + i];
                    var errorCode = response.Exception?.MessagingErrorCode;
                    if (errorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                    {
                        // Dead token — purge so we stop sending to it.
                        // Removing it is progress, so it does not count
                        // against the batch.
                        Console.WriteLine(
                            $"info: AdhocReminderJob - soft-deleting dead device token " +
                            $"{deviceToken.Id} (worker {deviceToken.WorkerId}, {errorCode})");
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

    private static void EnsureFirebaseApp(string serviceAccountJson)
    {
        if (FirebaseApp.DefaultInstance != null)
        {
            return;
        }

        lock (FirebaseInitLock)
        {
            if (FirebaseApp.DefaultInstance != null)
            {
                return;
            }

            FirebaseApp.Create(new AppOptions
            {
                // CredentialFactory is the non-obsolete replacement for
                // GoogleCredential.FromJson; pinning the generic to
                // ServiceAccountCredential also fails fast (into the job's
                // try/catch + Sentry) if the configured JSON is not a
                // service-account key.
                Credential = CredentialFactory
                    .FromJson<ServiceAccountCredential>(serviceAccountJson)
                    .ToGoogleCredential()
            });
        }
    }
}
