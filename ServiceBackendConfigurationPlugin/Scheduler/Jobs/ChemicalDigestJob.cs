using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.eFormApi.BasePn.Infrastructure.Database.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;
using Sentry;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>
/// flutter-chemistry weekly digest (spec §9): from Monday 07:00
/// Europe/Copenhagen, one Danish e-mail per address in any property's
/// ChemicalPropertySettings.DigestRecipients, one section per property, sent
/// only when something needs action. Not flag-gated: recipients opt in by being
/// listed (Plan D decision 9).
///
/// Once per week per tenant: the first SUCCESSFUL evaluation for the week's slot
/// closes the week, even when nothing needed action or nobody is listed (marker
/// below). So the digest is "Monday's picture"; an item that becomes due on
/// Wednesday waits for next Monday, and each tick after the first evaluation is
/// cheap. A missing customerNo or SendGrid key does NOT close the week. For the most recent slot only (a pod that was down
/// on Monday sends late in the same week; older slots are never sent). New
/// registrations are the window [previous slot, that slot).
///
/// Claim before send: the Channel Email ChemicalAlertLog rows are written BEFORE
/// the first e-mail and the job skips when any such row exists at or after the
/// slot, so a restart, a crash mid-run or a failure after the sends cannot resend.
/// If no e-mail at all was accepted the claim rows are removed and the next hourly
/// tick retries (decision 11). A failed recipient after a partial success is not
/// retried. When there is nothing to send, a PluginConfigurationValues row
/// (<see cref="EvaluatedSlotKey"/>, the slot as ISO 8601 UTC) is the marker that closes
/// the week; no schema change. Across pods a duplicate is possible only in the seconds between the
/// guard check and the claim write: GET_LOCK is node-local on Galera, and a unique
/// constraint would need a base migration, so that window is accepted.
/// </summary>
public class ChemicalDigestJob(
    BackendConfigurationDbContextHelper dbContextHelper,
    ChemicalDbContextHelper chemicalDbContextHelper,
    IChemicalSdkDirectory sdkDirectory,
    IChemicalEmailSender emailSender,
    TimeProvider time) : IJob
{
    /// <summary>Marker: the slot (ISO 8601 UTC) whose evaluation found nothing to send.</summary>
    public const string EvaluatedSlotKey = "BackendConfigurationSettings:ChemistryDigestEvaluatedSlot";

    // Overlapping hourly ticks (a slow SearchListJob can outlast the period) must
    // not run the job twice in one pod.
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public async Task Execute()
    {
        if (!await RunGate.WaitAsync(0))
        {
            Console.WriteLine("info: ChemicalDigestJob - previous run still active; skipping this tick");
            return;
        }

        try
        {
            await RunAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: ChemicalDigestJob - {e.Message}");
            SentrySdk.CaptureException(e);
        }
        finally
        {
            RunGate.Release();
        }
    }

    /// <summary>One tick. Public so tests can observe exceptions that Execute reports to Sentry.</summary>
    public async Task RunAsync()
    {
        var utcNow = time.GetUtcNow().UtcDateTime;
        if (ChemicalDigestSchedule.CurrentSlotStartUtc(utcNow) is not { } slot)
        {
            return;
        }

        await using var db = dbContextHelper.GetDbContext();
        if (await db.ChemicalAlertLogs.AnyAsync(x => x.Channel == ChemicalAlertChannelEnum.Email
                                              && x.WorkflowState != Constants.WorkflowStates.Removed
                                              && x.SentAt >= slot))
        {
            return;
        }

        var slotText = slot.ToString("O", CultureInfo.InvariantCulture);
        if (await db.PluginConfigurationValues.AsNoTracking().AnyAsync(x => x.Name == EvaluatedSlotKey && x.Value == slotText))
        {
            return;
        }

        var settings = await (
                from s in db.ChemicalPropertySettings
                join p in db.Properties on s.PropertyId equals p.Id
                where s.WorkflowState != Constants.WorkflowStates.Removed
                      && p.WorkflowState != Constants.WorkflowStates.Removed
                      && s.DigestRecipients != null && s.DigestRecipients != ""
                select new { s.PropertyId, p.Name, s.DigestRecipients })
            .AsNoTracking()
            .ToListAsync();
        if (settings.Count == 0)
        {
            await MarkEvaluatedAsync(db, slotText);
            return;
        }

        var propertyIds = settings.Select(s => s.PropertyId).ToList();
        var placements = await (
                from pl in db.ChemicalPlacements
                join l in db.ChemicalLocations on pl.LocationId equals l.Id
                where propertyIds.Contains(l.PropertyId)
                      && pl.RemovedAt == null
                      && pl.WorkflowState != Constants.WorkflowStates.Removed
                select new { l.PropertyId, pl.Id, pl.ChemicalId, LocationName = l.Name, pl.PlacementNote, pl.RegisteredAt, pl.MovedFromPlacementId })
            .AsNoTracking()
            .ToListAsync();
        var register = await ChemicalRegisterFactsReader.LoadAsync(
            chemicalDbContextHelper, placements.Select(p => p.ChemicalId).Distinct().ToList());

        var properties = settings
            .Select(s => new ChemicalDigestProperty(s.PropertyId, s.Name, ChemicalDigestRecipients.Parse(s.DigestRecipients),
                placements.Where(p => p.PropertyId == s.PropertyId)
                    .Select(p => new ChemicalDigestPlacement(p.Id, p.ChemicalId, p.LocationName, p.PlacementNote,
                        // EF returns DateTimeKind.Unspecified from MariaDB; the column holds UTC.
                        DateTime.SpecifyKind(p.RegisteredAt, DateTimeKind.Utc), p.MovedFromPlacementId))
                    .ToList()))
            .ToList();
        var today = ChemicalAlertRules.CopenhagenToday(utcNow);
        var digests = ChemicalDigestPlanner.Build(
            properties, register, today, ChemicalDigestSchedule.PreviousSlotStartUtc(slot), slot);
        if (digests.Count == 0)
        {
            await MarkEvaluatedAsync(db, slotText);
            return;
        }

        var customerNo = await ChemicalCustomerNo.TryReadAsync(
            sdkDirectory, nameof(ChemicalDigestJob), "chemical-digest-customerno-empty");
        if (string.IsNullOrEmpty(customerNo))
        {
            return;
        }

        var key = emailSender.ReadKey();
        if (string.IsNullOrEmpty(key))
        {
            const string missing = "ChemicalDigestJob - " + SendGridChemicalEmailSender.SendGridKeyId +
                                   " is not set although digest recipients are listed; nothing can be sent.";
            Console.WriteLine($"fail: {missing}");
            SentrySdk.CaptureMessage(missing, scope => scope.SetFingerprint(["chemical-digest-no-sendgrid-key"]), SentryLevel.Error);
            return;
        }

        var claims = await ClaimAsync(db, digests, utcNow);
        var delivered = 0;
        var failed = 0;
        var authRejected = false;
        foreach (var digest in digests)
        {
            try
            {
                await emailSender.SendAsync(key, ChemicalDigestEmailRenderer.Render(digest, customerNo, today));
                delivered++;
            }
            catch (ChemicalEmailAuthException e)
            {
                failed = digests.Count - delivered;
                authRejected = true;
                const string rejected = "ChemicalDigestJob - SendGrid rejected the key (401/403); stopping this run.";
                Console.WriteLine($"fail: {rejected} {e.Message}");
                SentrySdk.CaptureMessage(rejected, scope => scope.SetFingerprint(["chemical-digest-sendgrid-auth"]), SentryLevel.Error);
                break;
            }
            catch (Exception e)
            {
                // The address is deliberately not logged (personal data in logs).
                failed++;
                Console.WriteLine($"fail: ChemicalDigestJob - a digest e-mail was not accepted: {e.Message}");
            }
        }

        if (delivered == 0)
        {
            await ReleaseAsync(db, claims);
            Console.WriteLine("warn: ChemicalDigestJob - no digest was accepted; retrying on the next tick");
            if (!authRejected)
            {
                // At most one event per tick; the auth case already raised its own.
                CaptureUndelivered($"ChemicalDigestJob - none of {digests.Count} digest e-mail(s) was accepted; retrying on the next tick.");
            }

            return;
        }

        if (failed > 0)
        {
            CaptureUndelivered($"ChemicalDigestJob - {failed} of {digests.Count} digest e-mail(s) were not accepted and will not be retried this week.");
        }

        Console.WriteLine($"info: ChemicalDigestJob - sent {delivered} of {digests.Count} digest e-mail(s)");
    }

    /// <summary>One Email log row per distinct action item of the planned digests, written before any send.</summary>
    private static async Task<List<ChemicalAlertLog>> ClaimAsync(
        BackendConfigurationPnDbContext db, IReadOnlyList<ChemicalDigest> digests, DateTime utcNow)
    {
        var items = digests
            .SelectMany(d => d.Sections)
            .SelectMany(s => s.ActionItems)
            .DistinctBy(i => (i.PlacementId, i.Threshold, i.DeadlineKind, i.Status))
            .ToList();
        var claims = new List<ChemicalAlertLog>();
        foreach (var item in items)
        {
            var claim = new ChemicalAlertLog
            {
                PlacementId = item.PlacementId,
                Threshold = item.Threshold!.Value,
                DeadlineKind = item.DeadlineKind,
                Status = item.Status,
                Channel = ChemicalAlertChannelEnum.Email,
                SentAt = utcNow,
                CreatedByUserId = 0,
                UpdatedByUserId = 0,
            };
            try
            {
                await claim.Create(db);
            }
            catch
            {
                // All or nothing: a partial claim would suppress the week with nothing sent.
                db.Entry(claim).State = EntityState.Detached; // the failed insert must not be retried by the release
                await ReleaseAsync(db, claims);
                throw;
            }

            claims.Add(claim);
        }

        return claims;
    }

    private static async Task ReleaseAsync(BackendConfigurationPnDbContext db, List<ChemicalAlertLog> claims)
    {
        foreach (var claim in claims)
        {
            await claim.Delete(db);
        }
    }

    private static void CaptureUndelivered(string message)
    {
        Console.WriteLine($"fail: {message}");
        SentrySdk.CaptureMessage(message, scope => scope.SetFingerprint(["chemical-digest-undelivered"]), SentryLevel.Warning);
    }

    private static async Task MarkEvaluatedAsync(BackendConfigurationPnDbContext db, string slotText)
    {
        var marker = await db.PluginConfigurationValues.FirstOrDefaultAsync(x => x.Name == EvaluatedSlotKey);
        if (marker == null)
        {
            db.PluginConfigurationValues.Add(new PluginConfigurationValue { Name = EvaluatedSlotKey, Value = slotText });
        }
        else
        {
            marker.Value = slotText;
        }

        await db.SaveChangesAsync();
    }
}
