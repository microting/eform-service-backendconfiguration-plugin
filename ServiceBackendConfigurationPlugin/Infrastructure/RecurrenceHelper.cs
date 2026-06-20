using System;

namespace ServiceBackendConfigurationPlugin.Infrastructure;

public static class RecurrenceHelper
{
    // ordinal: 1..5 (1 = first). targetDow: 0=Sun..6=Sat (matches System.DayOfWeek).
    public static DateTime? NthWeekdayOfMonth(int year, int month, int ordinal, int targetDow)
    {
        var firstOfMonth = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        int dowOffset = (targetDow - (int)firstOfMonth.DayOfWeek + 7) % 7;
        var candidate = firstOfMonth.AddDays(dowOffset + (ordinal - 1) * 7);
        return candidate.Month != month ? null : candidate;
    }
}
