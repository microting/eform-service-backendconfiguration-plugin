namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using System.Globalization;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>
/// The latest "Monday 07:00 Europe/Copenhagen" slot of the current ISO week (spec §9) on both sides of DST:
/// CEST (UTC+2) → slot 05:00 UTC, CET (UTC+1) → slot 06:00 UTC.
/// DST ends 2026-10-25 and starts 2027-03-28.
/// </summary>
[TestFixture]
public class ChemicalDigestScheduleTests
{
    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    [TestCase("2026-10-19T04:59:00Z", null)]                    // Mon 06:59 CEST
    [TestCase("2026-10-19T05:00:00Z", "2026-10-19T05:00:00Z")]  // Mon 07:00 CEST
    [TestCase("2026-10-19T21:59:00Z", "2026-10-19T05:00:00Z")]  // Mon 23:59 CEST
    [TestCase("2026-10-19T22:00:00Z", "2026-10-19T05:00:00Z")]  // Tue 00:00 CEST: catch-up for Monday's slot
    [TestCase("2026-10-25T06:00:00Z", "2026-10-19T05:00:00Z")]  // Sunday of the switch: last Monday's slot, same week
    [TestCase("2026-10-26T05:30:00Z", null)]                    // Mon 06:30 CET
    [TestCase("2026-10-26T06:00:00Z", "2026-10-26T06:00:00Z")]  // Mon 07:00 CET
    [TestCase("2026-10-26T22:59:00Z", "2026-10-26T06:00:00Z")]  // Mon 23:59 CET
    [TestCase("2026-10-27T06:00:00Z", "2026-10-26T06:00:00Z")]  // Tuesday after the switch (CET slot)
    [TestCase("2026-10-28T10:00:00Z", "2026-10-26T06:00:00Z")]  // Wednesday
    [TestCase("2026-10-25T22:59:00Z", "2026-10-19T05:00:00Z")]  // Sunday 23:59 CET: still the CEST slot of the week
    [TestCase("2027-03-31T10:00:00Z", "2027-03-29T05:00:00Z")]  // Wednesday after the spring switch
    [TestCase("2027-03-22T05:59:00Z", null)]                    // Mon 06:59 CET
    [TestCase("2027-03-22T06:00:00Z", "2027-03-22T06:00:00Z")]  // Mon 07:00 CET
    [TestCase("2027-03-29T04:59:00Z", null)]                    // Mon 06:59 CEST
    [TestCase("2027-03-29T05:00:00Z", "2027-03-29T05:00:00Z")]  // Mon 07:00 CEST
    public void CurrentSlotStartUtc(string utcNow, string expectedSlot)
    {
        var slot = ChemicalDigestSchedule.CurrentSlotStartUtc(Utc(utcNow));

        Assert.That(slot, Is.EqualTo(expectedSlot == null ? null : Utc(expectedSlot)));
        if (slot is { } value)
        {
            Assert.That(value.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void CurrentSlotStartUtc_RejectsNonUtcInput()
    {
        Assert.That(() => ChemicalDigestSchedule.CurrentSlotStartUtc(new DateTime(2026, 10, 19, 7, 0, 0, DateTimeKind.Local)),
            Throws.ArgumentException);
    }

    // The previous slot is computed in Copenhagen local calendar time (same Monday
    // 07:00 one week earlier), so the DST week is 167 or 169 hours, not 168.
    [TestCase("2026-10-19T05:00:00Z", "2026-10-12T05:00:00Z")]  // CEST to CEST
    [TestCase("2026-10-26T06:00:00Z", "2026-10-19T05:00:00Z")]  // CET after the autumn switch: 169 h
    [TestCase("2027-03-22T06:00:00Z", "2027-03-15T06:00:00Z")]  // CET to CET
    [TestCase("2027-03-29T05:00:00Z", "2027-03-22T06:00:00Z")]  // CEST after the spring switch: 167 h
    public void PreviousSlotStartUtc_IsOneLocalWeekEarlier(string slot, string expected)
    {
        var previous = ChemicalDigestSchedule.PreviousSlotStartUtc(Utc(slot));

        Assert.That(previous, Is.EqualTo(Utc(expected)));
        Assert.That(previous.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void PreviousSlotStartUtc_RejectsNonUtcInput()
    {
        Assert.That(() => ChemicalDigestSchedule.PreviousSlotStartUtc(new DateTime(2026, 10, 26, 7, 0, 0, DateTimeKind.Local)),
            Throws.ArgumentException);
    }
}
