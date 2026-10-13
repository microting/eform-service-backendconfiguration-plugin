using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
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
/// Once per week per tenant: skipped when a Channel Email ChemicalAlertLog
/// exists at or after this week's slot, so a restart or a second pod does not
/// resend. Each action item of every delivered e-mail is logged once. If every
/// send fails nothing is logged and the next hourly tick in the window retries
/// (decision 11). New registrations are the window [previous slot, this slot).
/// </summary>
public class ChemicalDigestJob(
    BackendConfigurationDbContextHelper dbContextHelper,
    ChemicalDbContextHelper chemicalDbContextHelper,
    IChemicalSdkDirectory sdkDirectory,
    IChemicalEmailSender emailSender,
    TimeProvider time) : IJob
{
    private static bool _notConfiguredLogged;

    public async Task Execute()
    {
        try
        {
            await RunAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: ChemicalDigestJob - {e.Message}");
            SentrySdk.CaptureException(e);
        }
    }

    public async Task RunAsync()
    {
        var utcNow = time.GetUtcNow().UtcDateTime;
        if (ChemicalDigestSchedule.CurrentSlotStartUtc(utcNow) is not { } slot)
        {
            return;
        }

        await using var db = dbContextHelper.GetDbContext();
        if (await db.ChemicalAlertLogs.AnyAsync(x => x.Channel == ChemicalAlertChannelEnum.Email && x.SentAt >= slot))
        {
            return;
        }

        if (!emailSender.IsConfigured())
        {
            if (!_notConfiguredLogged)
            {
                Console.WriteLine($"warn: ChemicalDigestJob - {SendGridChemicalEmailSender.SendGridKeyId} is not set; skipping");
                _notConfiguredLogged = true;
            }

            return;
        }

        _notConfiguredLogged = false;

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
            return;
        }

        var customerNo = await sdkDirectory.CustomerNoAsync();
        var delivered = new List<ChemicalDigest>();
        foreach (var digest in digests)
        {
            try
            {
                await emailSender.SendAsync(ChemicalDigestEmailRenderer.Render(digest, customerNo, today));
                delivered.Add(digest);
            }
            catch (Exception e)
            {
                // The address is deliberately not logged (personal data in logs).
                Console.WriteLine($"fail: ChemicalDigestJob - a digest e-mail was not accepted: {e.Message}");
                SentrySdk.CaptureException(e);
            }
        }

        if (delivered.Count == 0)
        {
            Console.WriteLine("warn: ChemicalDigestJob - no digest was accepted; retrying on the next tick in the window");
            return;
        }

        var items = delivered
            .SelectMany(d => d.Sections)
            .SelectMany(s => s.ActionItems)
            .DistinctBy(i => (i.PlacementId, i.Threshold, i.DeadlineKind, i.Status))
            .ToList();
        foreach (var item in items)
        {
            await new ChemicalAlertLog
            {
                PlacementId = item.PlacementId,
                Threshold = item.Threshold!.Value,
                DeadlineKind = item.DeadlineKind,
                Status = item.Status,
                Channel = ChemicalAlertChannelEnum.Email,
                SentAt = utcNow,
                CreatedByUserId = 0,
                UpdatedByUserId = 0,
            }.Create(db);
        }

        Console.WriteLine($"info: ChemicalDigestJob - sent {delivered.Count} of {digests.Count} digest e-mail(s)");
    }
}
