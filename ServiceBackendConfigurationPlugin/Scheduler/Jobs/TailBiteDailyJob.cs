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
using System.Data;
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
/// Daily tail-bite (halebid) job: pushes follow-up reminders to the
/// responsible worker and the property's managers, then soft-deletes orphan
/// photos and stale upload placeholders. Mirrors <see cref="AdhocReminderJob"/>; the halebid app has its OWN
/// Firebase project, so it uses its own credential, a NAMED FirebaseApp and
/// device registrations with AppId == <see cref="HalebidAppId"/>.
///
/// One reminder per action per day: <c>LastReminderAt</c> is written after the
/// send, so a restart the same day does not re-send.
///
/// Orphan cleanup soft-deletes the TailBiteRegistrationPhoto rows only; the
/// SDK UploadedData rows and the S3 objects remain.
/// </summary>
public class TailBiteDailyJob : IJob
{
    private const string ServiceAccountJsonKey =
        "BackendConfigurationSettings:HalebidFirebaseServiceAccountJson";

    private const int FcmBatchLimit = 500;

    /// <summary>Must match the AppId the halebid app registers with (plugin Task 12 sender uses the same).</summary>
    public const string HalebidAppId = "halebid";

    public const string FirebaseAppName = "microting-halebid";

    private static readonly object FirebaseInitLock = new();

    private static bool _missingCredentialsLogged;

    private readonly BackendConfigurationDbContextHelper _dbContextHelper;

    public TailBiteDailyJob(BackendConfigurationDbContextHelper dbContextHelper)
    {
        _dbContextHelper = dbContextHelper;
    }

    public async Task Execute()
    {
        await using var db = _dbContextHelper.GetDbContext();

        try
        {
            await SendReminders(db);
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: TailBiteDailyJob reminders - {e.Message}");
            SentrySdk.CaptureException(e);
        }

        try
        {
            await CleanupOrphanPhotos(db);
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: TailBiteDailyJob photo cleanup - {e.Message}");
            SentrySdk.CaptureException(e);
        }

        try
        {
            await CleanupStalePlaceholders(db);
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: TailBiteDailyJob placeholder cleanup - {e.Message}");
            SentrySdk.CaptureException(e);
        }
    }

    /// <summary>
    /// Live halebid-app registrations owned by any of <paramref name="sdkSiteIds"/>.
    /// The AppId predicate must stay: the DeviceTokens table is shared with other
    /// apps minted in other Firebase projects (see AdhocReminderJob.SelectRecipientTokens).
    /// Public so the tests run the shipped predicate.
    /// </summary>
    public static IQueryable<DeviceToken> SelectRecipientTokens(
        IQueryable<DeviceToken> deviceTokens, List<int> sdkSiteIds)
    {
        return deviceTokens
            .Where(x => x.AppId == HalebidAppId)
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created)
            .Where(x => sdkSiteIds.Contains(x.SdkSiteId));
    }

    private async Task SendReminders(BackendConfigurationPnDbContext db)
    {
        var now = DateTime.UtcNow;
        var reminders = TailBiteReminderSelector.Select(
            db.TailBiteAssessmentActions, db.TailBiteRiskAssessments, db.TailBiteOutbreaks, db.PropertyWorkers, now);
        if (reminders.Count == 0)
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
                    $"warn: TailBiteDailyJob - {ServiceAccountJsonKey} is not set; " +
                    $"skipping {reminders.Count} due reminder(s)");
                _missingCredentialsLogged = true;
            }

            return;
        }

        _missingCredentialsLogged = false;

        var messaging = EnsureHalebidMessaging(serviceAccountJson);

        foreach (var reminder in reminders)
        {
            try
            {
                await SendReminder(db, messaging, reminder);
            }
            catch (Exception e)
            {
                // A failing reminder keeps its marker unset; it must not sink the rest.
                Console.WriteLine($"fail: TailBiteDailyJob action {reminder.ActionId} - {e.Message}");
                SentrySdk.CaptureException(e);
            }
        }
    }

    private static async Task SendReminder(
        BackendConfigurationPnDbContext db, FirebaseMessaging messaging, TailBiteReminder reminder)
    {
        var recipients = reminder.RecipientSiteIds.ToList();

        var devices = recipients.Count == 0
            ? new List<DeviceToken>()
            : await SelectRecipientTokens(db.DeviceTokens, recipients).ToListAsync();

        // No halebid registrations among the recipients: nothing is sent, yet the
        // action is still marked below. The reminder is consumed for today and the
        // action stays due, so it is tried again tomorrow.
        var outcome = await SendAndProcessBatchAsync(db, messaging, reminder, BuildFcmMessages(reminder, devices), devices);

        if (outcome.SystemicFault || (outcome.TransientFailures > 0 && outcome.Delivered == 0))
        {
            // Marker left unset: the next daily run retries.
            Console.WriteLine(
                $"warn: TailBiteDailyJob - action {reminder.ActionId} not marked reminded " +
                $"(systemic fault: {outcome.SystemicFault}, transient failures: {outcome.TransientFailures})");
            return;
        }

        await MarkRemindedLocked(db, reminder);
    }

    /// <summary>
    /// Writes LastReminderAt under the plugin's per-property lock (the same
    /// <c>TailBiteProperties</c> row lock the plugin takes), so a concurrent done / withdraw /
    /// remove in the plugin and this write are serialised. The action is re-queried inside the
    /// lock and left untouched if it is no longer remindable.
    /// </summary>
    private static Task MarkRemindedLocked(BackendConfigurationPnDbContext db, TailBiteReminder reminder)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            // Detach anything a failed earlier attempt left pending.
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT `Id` FROM `TailBiteProperties` WHERE `PropertyId` = {reminder.PropertyId} FOR UPDATE");
                var action = await db.TailBiteAssessmentActions.FirstOrDefaultAsync(x => x.Id == reminder.ActionId);
                if (action != null && TailBiteReminderSelector.IsStillRemindable(action))
                {
                    action.LastReminderAt = DateTime.UtcNow;
                    await action.Update(db);
                }

                await tx.CommitAsync();
            }
            catch
            {
                db.ChangeTracker.Clear();
                try { await tx.RollbackAsync(); }
                catch { /* the original exception is the one that matters */ }
                throw;
            }
        });
    }

    /// <summary>
    /// True when EVERY response in a batch failed with the SAME code and that code is one a
    /// server-side fault can produce (SenderIdMismatch: wrong credential; InvalidArgument:
    /// malformed payload). Such a batch says nothing about the devices, so nothing is pruned.
    /// Mirrors the "systemic" guard in the plugin's PushNotificationService. Unregistered is
    /// deliberately not systemic: it only ever means that registration is gone.
    /// Public so the tests run the shipped rule.
    /// </summary>
    public static bool IsSystemicBatch(IReadOnlyList<MessagingErrorCode?> outcomes)
    {
        return outcomes.Count > 0
               && outcomes[0] is MessagingErrorCode.SenderIdMismatch or MessagingErrorCode.InvalidArgument
               && outcomes.All(x => x == outcomes[0]);
    }

    private readonly record struct SendOutcome(int Delivered, int TransientFailures, bool SystemicFault);

    private static List<Message> BuildFcmMessages(TailBiteReminder reminder, List<DeviceToken> devices)
    {
        return devices.Select(deviceToken => new Message
        {
            // See AdhocReminderJob: FCM v1 only accepts registration ids via this field.
#pragma warning disable CS0618
            Token = deviceToken.FcmToken,
#pragma warning restore CS0618
            Notification = new Notification
            {
                Title = "Opfølgning på halebid",
                Body = reminder.Description
            },
            Data = new Dictionary<string, string>
            {
                ["type"] = "tailbite_action",
                ["outbreak_id"] = reminder.OutbreakId.ToString()
            }
        }).ToList();
    }

    private static async Task<SendOutcome> SendAndProcessBatchAsync(
        BackendConfigurationPnDbContext db, FirebaseMessaging messaging, TailBiteReminder reminder,
        List<Message> messages, List<DeviceToken> devices)
    {
        var delivered = 0;
        var transientFailures = 0;
        var systemicFault = false;

        for (var offset = 0; offset < messages.Count; offset += FcmBatchLimit)
        {
            var chunk = messages.Skip(offset).Take(FcmBatchLimit).ToList();
            var batch = await messaging.SendEachAsync(chunk);

            var outcomes = batch.Responses
                .Select(x => x.IsSuccess ? null : x.Exception?.MessagingErrorCode)
                .ToList();

            if (IsSystemicBatch(outcomes))
            {
                // Wrong credential or malformed payload, not dead registrations: prune nothing.
                var fault = $"TailBiteDailyJob - all {outcomes.Count} message(s) for action " +
                            $"{reminder.ActionId} failed with {outcomes[0]}; check {ServiceAccountJsonKey} and the payload.";
                Console.WriteLine($"fail: {fault}");
                SentrySdk.CaptureMessage(fault, SentryLevel.Error);
                systemicFault = true;
                continue;
            }

            for (var i = 0; i < batch.Responses.Count; i++)
            {
                if (batch.Responses[i].IsSuccess)
                {
                    delivered++;
                    continue;
                }

                if (outcomes[i] is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                {
                    await devices[offset + i].Delete(db);
                }
                else
                {
                    transientFailures++;
                }
            }
        }

        return new SendOutcome(delivered, transientFailures, systemicFault);
    }

    private static async Task CleanupOrphanPhotos(BackendConfigurationPnDbContext db)
    {
        var orphans = await TailBiteReminderSelector
            .OrphanPhotos(db.TailBiteRegistrationPhotos, db.TailBiteRegistrations, DateTime.UtcNow)
            .ToListAsync();
        foreach (var photo in orphans)
        {
            // Re-check with a fresh query right before deleting: a registration may have been
            // synced for this photo since the list above was read. This narrows, not closes,
            // the window (a registration can still land between this check and the delete);
            // that is acceptable because only photos older than 30 days are considered.
            var ownedNow = await db.TailBiteRegistrations.AnyAsync(r =>
                r.ClientUuid == photo.RegistrationClientUuid
                && r.PropertyId == photo.PropertyId
                && r.SiteId == photo.UploadedBySiteId);
            if (ownedNow)
            {
                continue;
            }

            await photo.Delete(db);
        }
    }

    /// <summary>
    /// Soft-deletes photo reservations (SdkUploadedDataId == 0) whose upload never completed.
    /// </summary>
    private static async Task CleanupStalePlaceholders(BackendConfigurationPnDbContext db)
    {
        var stale = await TailBiteReminderSelector
            .StalePlaceholders(db.TailBiteRegistrationPhotos, DateTime.UtcNow)
            .ToListAsync();
        foreach (var photo in stale)
        {
            await photo.Delete(db);
        }
    }

    /// <summary>
    /// The FirebaseMessaging client for the halebid project: a NAMED FirebaseApp
    /// created once (never the process-wide default; see
    /// <see cref="AdhocReminderJob.EnsureAdhocMessaging"/> for why).
    /// </summary>
    public static FirebaseMessaging EnsureHalebidMessaging(string serviceAccountJson)
    {
        var app = FirebaseApp.GetInstance(FirebaseAppName);
        if (app == null)
        {
            lock (FirebaseInitLock)
            {
                app = FirebaseApp.GetInstance(FirebaseAppName);
                if (app == null)
                {
                    var options = new AppOptions
                    {
                        Credential = CredentialFactory
                            .FromJson<ServiceAccountCredential>(serviceAccountJson)
                            .ToGoogleCredential()
                    };

                    app = FirebaseApp.Create(options, FirebaseAppName);
                }
            }
        }

        return FirebaseMessaging.GetMessaging(app);
    }
}
