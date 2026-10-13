using System;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>
/// The weekly digest is due from Monday 07:00 Europe/Copenhagen (spec §9, Plan D
/// decision 11) and, if that slot was missed (pod down, SendGrid out), for the rest
/// of the same ISO week. Older slots are never sent. The hourly tick is not aligned
/// to the hour, so the job asks "which slot are we in" rather than "is it 07:00".
/// </summary>
public static class ChemicalDigestSchedule
{
    public static readonly TimeOnly SendAt = new(7, 0);

    /// <summary>
    /// This week's Monday 07:00 local as UTC once it has passed (Monday 07:00 through
    /// Sunday 23:59 local); null on Monday before 07:00, when last week's slot is too old.
    /// </summary>
    public static DateTime? CurrentSlotStartUtc(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("utcNow must be a UTC DateTime.", nameof(utcNow));
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ChemicalAlertRules.Copenhagen);
        if (local.DayOfWeek == DayOfWeek.Monday && TimeOnly.FromDateTime(local) < SendAt)
        {
            return null;
        }

        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        // 07:00 is never inside a DST gap or overlap (EU switches at 02:00/03:00),
        // so the local-to-UTC conversion is unambiguous.
        return SlotUtc(local.Date.AddDays(-daysSinceMonday));
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
        return SlotUtc(local.Date.AddDays(-7));
    }

    /// <summary>SendAt on the given Copenhagen calendar date, as UTC.</summary>
    private static DateTime SlotUtc(DateTime localDate) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localDate + SendAt.ToTimeSpan(), DateTimeKind.Unspecified), ChemicalAlertRules.Copenhagen);
}
