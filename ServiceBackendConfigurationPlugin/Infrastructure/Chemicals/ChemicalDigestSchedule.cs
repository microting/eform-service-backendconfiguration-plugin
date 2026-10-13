using System;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>
/// The weekly digest is due from Monday 07:00 to Monday 23:59 Europe/Copenhagen
/// (spec §9, Plan D decision 11). The hourly tick is not aligned to the hour, so
/// the job asks "which slot are we in" rather than "is it 07:00".
/// </summary>
public static class ChemicalDigestSchedule
{
    public static readonly TimeOnly SendAt = new(7, 0);

    /// <summary>This Monday's 07:00 local as UTC while inside the window; null otherwise.</summary>
    public static DateTime? CurrentSlotStartUtc(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("utcNow must be a UTC DateTime.", nameof(utcNow));
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ChemicalAlertRules.Copenhagen);
        if (local.DayOfWeek != DayOfWeek.Monday || TimeOnly.FromDateTime(local) < SendAt)
        {
            return null;
        }

        // 07:00 is never inside a DST gap or overlap (EU switches at 02:00/03:00),
        // so the local-to-UTC conversion is unambiguous.
        var slotLocal = DateTime.SpecifyKind(local.Date + SendAt.ToTimeSpan(), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(slotLocal, ChemicalAlertRules.Copenhagen);
    }

    /// <summary>
    /// The slot one week before <paramref name="slotUtc"/>, computed in Copenhagen
    /// calendar time (same Monday 07:00 local, seven local days earlier) and then
    /// converted to UTC. In the DST week this is 167 or 169 hours, not 168, so
    /// consecutive windows [previous, slot) neither gap nor overlap.
    /// </summary>
    public static DateTime PreviousSlotStartUtc(DateTime slotUtc)
    {
        if (slotUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("slotUtc must be a UTC DateTime.", nameof(slotUtc));
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(slotUtc, ChemicalAlertRules.Copenhagen);
        var previousLocal = DateTime.SpecifyKind(local.Date.AddDays(-7) + SendAt.ToTimeSpan(), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(previousLocal, ChemicalAlertRules.Copenhagen);
    }
}
