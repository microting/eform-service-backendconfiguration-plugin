using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;
using Sentry;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>
/// flutter-chemistry deadline and status push (spec §9), once a day in the
/// 05:00 UTC hourly tick, after the 02:00 UTC register sync.
///
/// Opt-in per tenant, like AdhocReminderJob: nothing happens unless
/// <see cref="FeatureFlagKey"/> is "true" (absent = off; ops switch it on
/// with setup_firebase_push.py --app chemistry --with-reminder-flag). The FCM
/// credential is <see cref="ServiceAccountJsonKey"/> (Firebase project
/// microting-chemistry). Recipients are the SDK sites with Admin or Remove on
/// the property that are still active property workers; tokens are
/// DeviceTokens with AppId <see cref="ChemistryAppId"/>.
///
/// Dedupe is ChemicalAlertLog (Channel Push), across the MovedFromPlacementId
/// lineage (ChemicalAlertPlanner). A property's alerts are logged only when its
/// send had no transient failure; a systemic fault (every send SenderIdMismatch
/// or every send InvalidArgument) prunes nothing and logs nothing. When only
/// some sends fail with InvalidArgument, those tokens are pruned like dead ones. See Plan D decisions 3–8.
/// </summary>
public class ChemicalAlertJob(
    BackendConfigurationDbContextHelper dbContextHelper,
    ChemicalDbContextHelper chemicalDbContextHelper,
    IChemicalSdkDirectory sdkDirectory,
    IChemicalPushSender pushSender,
    TimeProvider time) : IJob
{
    public const string FeatureFlagKey = "BackendConfigurationSettings:ChemistryAlertPushEnabled";

    public const string ServiceAccountJsonKey = "BackendConfigurationSettings:ChemistryFirebaseServiceAccountJson";

    /// <summary>
    /// DeviceTokens.AppId flutter-chemistry registers with (Plan D Contract C2).
    /// Replicated in flutter-chemistry; nothing enforces agreement — see the
    /// same warning on AdhocReminderJob.AdhocAppId.
    /// </summary>
    public const string ChemistryAppId = "chemistry";

    public const int RunHourUtc = 5;

    // "Log once + skip" latch for a missing credential; resets when it appears.
    private static bool _missingCredentialsLogged;

    public async Task Execute()
    {
        try
        {
            await RunAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: ChemicalAlertJob - {e.Message}");
            SentrySdk.CaptureException(e);
        }
    }

    /// <summary>One tick. Public so tests can observe exceptions that Execute reports to Sentry.</summary>
    public async Task RunAsync()
    {
        var utcNow = time.GetUtcNow().UtcDateTime;
        if (utcNow.Hour != RunHourUtc)
        {
            return;
        }

        await using var db = dbContextHelper.GetDbContext();
        if (!bool.TryParse(await ReadValueAsync(db, FeatureFlagKey), out var enabled) || !enabled)
        {
            return;
        }

        var serviceAccountJson = await ReadValueAsync(db, ServiceAccountJsonKey);
        if (string.IsNullOrEmpty(serviceAccountJson))
        {
            if (!_missingCredentialsLogged)
            {
                Console.WriteLine($"warn: ChemicalAlertJob - {ServiceAccountJsonKey} is not set; skipping");
                _missingCredentialsLogged = true;
            }

            return;
        }

        _missingCredentialsLogged = false;

        var open = await (
                from placement in db.ChemicalPlacements
                join location in db.ChemicalLocations on placement.LocationId equals location.Id
                join property in db.Properties on location.PropertyId equals property.Id
                where placement.RemovedAt == null
                      && placement.WorkflowState != Constants.WorkflowStates.Removed
                      && property.WorkflowState != Constants.WorkflowStates.Removed
                select new { placement.Id, location.PropertyId, placement.ChemicalId, placement.ObservedStatus })
            .AsNoTracking()
            .ToListAsync();
        if (open.Count == 0)
        {
            return;
        }

        var movedFrom = await db.ChemicalPlacements.AsNoTracking()
            .Where(p => p.MovedFromPlacementId != null)
            .Select(p => new { p.Id, p.MovedFromPlacementId })
            .ToDictionaryAsync(x => x.Id, x => x.MovedFromPlacementId);
        var placements = open
            .Select(p => new ChemicalAlertPlacement(p.Id, p.PropertyId, p.ChemicalId, p.ObservedStatus,
                ChemicalAlertPlanner.Lineage(p.Id, movedFrom)))
            .ToList();
        var lineageIds = placements.SelectMany(p => p.Lineage).Distinct().ToList();
        var sentLog = await db.ChemicalAlertLogs.AsNoTracking()
            .Where(x => x.Channel == ChemicalAlertChannelEnum.Push && lineageIds.Contains(x.PlacementId))
            .Select(x => new ChemicalAlertLogKey(x.PlacementId, x.Threshold, x.DeadlineKind, x.Status))
            .ToListAsync();
        var register = await ChemicalRegisterFactsReader.LoadAsync(
            chemicalDbContextHelper, placements.Select(p => p.ChemicalId).Distinct().ToList());

        var plan = ChemicalAlertPlanner.Plan(placements, register, sentLog, ChemicalAlertRules.CopenhagenToday(utcNow));
        await StoreObservedStatusAsync(db, plan.ObservedStatusUpdates);
        if (plan.DueAlerts.Count == 0)
        {
            return;
        }

        var customerNo = await TryReadCustomerNoAsync();
        if (string.IsNullOrEmpty(customerNo))
        {
            return;
        }

        var systemicProperties = new List<int>();

        foreach (var property in plan.DueAlerts.GroupBy(a => a.PropertyId))
        {
            try
            {
                if (!await SendForPropertyAsync(db, serviceAccountJson, customerNo, property.Key, property.ToList(), utcNow))
                {
                    systemicProperties.Add(property.Key);
                }
            }
            catch (Exception e)
            {
                // Nothing was logged for this property: it retries tomorrow and
                // must not sink the other properties.
                Console.WriteLine($"fail: ChemicalAlertJob property {property.Key} - {e.Message}");
                SentrySdk.CaptureException(e);
            }
        }

        if (systemicProperties.Count > 0)
        {
            // Once per run, however many properties were hit.
            SentrySdk.CaptureMessage(
                $"ChemicalAlertJob - every message failed with the same systemic fault (SenderIdMismatch or " +
                $"InvalidArgument) for propert{(systemicProperties.Count == 1 ? "y" : "ies")} " +
                $"{string.Join(", ", systemicProperties)}: check {ServiceAccountJsonKey} and the payload. " +
                "No tokens were pruned.", SentryLevel.Error);
        }
    }

    /// <summary>
    /// The tenant's customerNo, or null when the SDK setting is missing or empty
    /// (reported to Sentry; the run then sends nothing and retries tomorrow).
    /// The directory throws on a missing setting, so that is caught here.
    /// </summary>
    private async Task<string> TryReadCustomerNoAsync()
    {
        try
        {
            var customerNo = await sdkDirectory.CustomerNoAsync();
            if (!string.IsNullOrEmpty(customerNo))
            {
                return customerNo;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: ChemicalAlertJob - customerNo unavailable: {e.Message}");
            SentrySdk.CaptureException(e);
            return null;
        }

        const string empty = "ChemicalAlertJob - the SDK setting customerNo is empty; sending nothing this run.";
        Console.WriteLine($"fail: {empty}");
        SentrySdk.CaptureMessage(empty, SentryLevel.Error);
        return null;
    }

    /// <summary>
    /// True when every outcome of a send is the same systemic fault: a wrong
    /// credential (SenderIdMismatch) or a rejected payload (InvalidArgument).
    /// Then the tokens are not at fault. Same rule as TailBiteDailyJob.IsSystemicBatch;
    /// Unregistered (DeadToken) is deliberately not systemic. Public so tests run the shipped rule.
    /// </summary>
    public static bool IsSystemicBatch(IReadOnlyList<ChemicalPushOutcome> outcomes) =>
        outcomes.Count > 0
        && outcomes[0] is ChemicalPushOutcome.SenderIdMismatch or ChemicalPushOutcome.InvalidArgument
        && outcomes.All(o => o == outcomes[0]);

    /// <summary>SDK site ids with Admin or Remove on the property that are still active workers there.</summary>
    public static IQueryable<int> SelectRecipientSiteIds(
        IQueryable<ChemicalWorkerPermission> permissions, IQueryable<PropertyWorker> propertyWorkers, int propertyId) =>
        permissions
            .Where(p => p.PropertyId == propertyId
                        && p.WorkflowState != Constants.WorkflowStates.Removed
                        && (p.Admin || p.Remove))
            .Where(p => propertyWorkers.Any(w => w.PropertyId == propertyId
                                                 && w.WorkerId == p.WorkerId
                                                 && w.WorkflowState != Constants.WorkflowStates.Removed))
            .Select(p => p.WorkerId)
            .Distinct();

    /// <summary>Live chemistry-app tokens of the given sites (AppId equality keeps the index usable).</summary>
    public static IQueryable<DeviceToken> SelectRecipientTokens(IQueryable<DeviceToken> deviceTokens, List<int> sdkSiteIds) =>
        deviceTokens
            .Where(x => x.AppId == ChemistryAppId)
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created)
            .Where(x => sdkSiteIds.Contains(x.SdkSiteId));

    /// <summary>Returns false when the send was a systemic fault (nothing pruned, nothing logged).</summary>
    private async Task<bool> SendForPropertyAsync(
        BackendConfigurationPnDbContext db, string serviceAccountJson, string customerNo, int propertyId,
        IReadOnlyList<ChemicalDueAlert> alerts, DateTime utcNow)
    {
        var propertyName = await db.Properties.Where(p => p.Id == propertyId).Select(p => p.Name).SingleAsync();
        var siteIds = await SelectRecipientSiteIds(db.ChemicalWorkerPermissions, db.PropertyWorkers, propertyId).ToListAsync();
        List<DeviceToken> tokens = siteIds.Count == 0 ? [] : await SelectRecipientTokens(db.DeviceTokens, siteIds).ToListAsync();

        if (tokens.Count > 0)
        {
            var languages = await sdkDirectory.LanguageCodesAsync(tokens.Select(t => t.SdkSiteId).Distinct().ToList());
            var sends = tokens
                .SelectMany(token => ChemicalPushMessages
                    .Compose(propertyId, propertyName, alerts, languages.GetValueOrDefault(token.SdkSiteId), customerNo)
                    .Select(content => (Token: token, Message: ChemicalPushMessages.ToFcmMessage(content, token.FcmToken))))
                .ToList();
            var outcomes = await pushSender.SendAsync(serviceAccountJson, sends.Select(s => s.Message).ToList());

            if (IsSystemicBatch(outcomes))
            {
                // Wrong credential or rejected payload, not dead registrations:
                // prune NOTHING, log nothing, retry on the next run.
                Console.WriteLine($"fail: ChemicalAlertJob - all {outcomes.Count} message(s) for property {propertyId} " +
                                  $"failed with {outcomes[0]}; pruning nothing");
                return false;
            }

            var transientFailures = 0;
            var deadTokens = new HashSet<DeviceToken>();
            for (var i = 0; i < outcomes.Count; i++)
            {
                switch (outcomes[i])
                {
                    case ChemicalPushOutcome.Transient:
                        transientFailures++;
                        break;
                    case ChemicalPushOutcome.SenderIdMismatch:
                        // Invariant violation: the AppId filter should make this
                        // unreachable. Prune it (like adhoc), but loudly.
                        SentrySdk.CaptureMessage(
                            $"ChemicalAlertJob - SenderIdMismatch on device token {sends[i].Token.Id} despite the " +
                            $"AppId == '{ChemistryAppId}' filter; soft-deleting it.", SentryLevel.Warning);
                        deadTokens.Add(sends[i].Token);
                        break;
                    case ChemicalPushOutcome.DeadToken:
                    case ChemicalPushOutcome.InvalidArgument: // not systemic here: others got through
                        deadTokens.Add(sends[i].Token);
                        break;
                }
            }

            foreach (var token in deadTokens)
            {
                Console.WriteLine($"info: ChemicalAlertJob - soft-deleting dead device token {token.Id} (site {token.SdkSiteId})");
                await token.Delete(db);
            }

            if (transientFailures > 0)
            {
                Console.WriteLine($"warn: ChemicalAlertJob - {transientFailures} transient send failure(s) for property " +
                                  $"{propertyId}; nothing logged, retrying on the next run");
                return true;
            }
        }

        await LogAsync(db, alerts, utcNow);
        return true;
    }

    /// <summary>Writes the Push logs and, for status alerts, the new ObservedStatus — atomically.</summary>
    private static async Task LogAsync(BackendConfigurationPnDbContext db, IReadOnlyList<ChemicalDueAlert> alerts, DateTime utcNow)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var alert in alerts)
            {
                await new ChemicalAlertLog
                {
                    PlacementId = alert.PlacementId,
                    Threshold = alert.Threshold,
                    DeadlineKind = alert.DeadlineKind,
                    Status = alert.Status,
                    Channel = ChemicalAlertChannelEnum.Push,
                    SentAt = utcNow,
                    CreatedByUserId = 0,
                    UpdatedByUserId = 0,
                }.Create(db);
            }

            var newStatuses = alerts
                .Where(a => a.Threshold == ChemicalAlertThresholdEnum.StatusChanged)
                .ToDictionary(a => a.PlacementId, a => a.Status);
            await StoreObservedStatusAsync(db, newStatuses);
            await transaction.CommitAsync();
        });
    }

    private static async Task StoreObservedStatusAsync(BackendConfigurationPnDbContext db, IReadOnlyDictionary<int, int?> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var ids = updates.Keys.ToList();
        var rows = await db.ChemicalPlacements.Where(p => ids.Contains(p.Id)).ToListAsync();
        foreach (var row in rows)
        {
            row.ObservedStatus = updates[row.Id];
            await row.Update(db);
        }
    }

    private static Task<string> ReadValueAsync(BackendConfigurationPnDbContext db, string name) =>
        db.PluginConfigurationValues.AsNoTracking().Where(x => x.Name == name).Select(x => x.Value).FirstOrDefaultAsync();
}
