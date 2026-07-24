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
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Pure due-instant evaluation for adhoc-task reminder pushes.
///
/// A reminder instant is the reminder DATE (VisibleFrom/Deadline) at
/// <c>*ReminderTimeMinutes</c> from midnight (default 480 = 08:00), compared
/// in server-local time. A reminder is DUE when <paramref name="now"/> has
/// passed the instant AND the corresponding <c>Last*ReminderSentAt</c> marker
/// has not been written at/after that instant — the marker comparison is what
/// makes the hourly <see cref="Scheduler.Jobs.AdhocReminderJob"/> idempotent.
///
/// <c>DeadlineReminderRepeat == 1</c> ("hverdage") re-fires every weekday
/// (Mon-Fri) after the deadline until the task is completed/archived; the
/// per-day marker comparison gives once-per-day idempotency. Repeat == 0
/// fires once (late catch-up allowed — the instant is fixed, so a task
/// evaluated after its instant still fires until the marker is set).
///
/// Kept free of DbContext/clock/Firebase dependencies so it is unit-testable.
/// </summary>
public static class AdhocReminderEvaluator
{
    /// <summary>
    /// Returns the due instant for the visible-from reminder when a send is
    /// currently due, otherwise null.
    /// </summary>
    public static DateTime? DueVisibleReminderInstant(AdhocTaskEntity task, DateTime now)
    {
        if (!task.VisibleReminder || task.VisibleFrom == null)
        {
            return null;
        }

        var dueInstant = task.VisibleFrom.Value.Date.AddMinutes(task.VisibleReminderTimeMinutes);
        if (now < dueInstant)
        {
            return null;
        }

        return IsMarkerStale(task.LastVisibleReminderSentAt, dueInstant) ? dueInstant : null;
    }

    /// <summary>
    /// Returns the due instant for the deadline reminder when a send is
    /// currently due, otherwise null.
    /// </summary>
    public static DateTime? DueDeadlineReminderInstant(AdhocTaskEntity task, DateTime now)
    {
        if (!task.DeadlineReminder || task.Deadline == null)
        {
            return null;
        }

        var deadlineDate = task.Deadline.Value.Date;
        var firstInstant = deadlineDate.AddMinutes(task.DeadlineReminderTimeMinutes);
        if (now < firstInstant)
        {
            return null;
        }

        if (task.DeadlineReminderRepeat != 1)
        {
            // Fire once. The instant is fixed at the deadline date, so a
            // missed hour (or a whole missed day) still catches up until the
            // marker is written.
            return IsMarkerStale(task.LastDeadlineReminderSentAt, firstInstant) ? firstInstant : null;
        }

        // Weekday repeat ("hverdage"): the deadline-day fire itself is NOT
        // weekday-guarded (mirrors Repeat == 0 semantics); every day after
        // the deadline only weekdays re-fire, and only once today's slot has
        // been reached — a slot missed for a whole day is skipped, not
        // replayed the following morning.
        DateTime currentInstant;
        if (now.Date == deadlineDate)
        {
            currentInstant = firstInstant;
        }
        else
        {
            if (!IsWeekday(now.DayOfWeek))
            {
                return null;
            }

            currentInstant = now.Date.AddMinutes(task.DeadlineReminderTimeMinutes);
            if (now < currentInstant)
            {
                return null;
            }
        }

        return IsMarkerStale(task.LastDeadlineReminderSentAt, currentInstant) ? currentInstant : null;
    }

    /// <summary>
    /// Decides whether the <c>Last*ReminderSentAt</c> marker may be written
    /// after a send attempt.
    ///
    /// Any transient failure blocks the marker (whole task+kind retries next
    /// hour). Any actual delivery writes it. With ZERO deliveries (no
    /// registered tokens, or every token was dead and got purged) the kind
    /// matters: a weekday-repeat deadline reminder self-heals next weekday,
    /// so today's slot may be marked done; a ONE-SHOT reminder
    /// (visible-from, or deadline with Repeat != 1) has a fixed instant that
    /// never recurs — writing the marker would permanently and silently lose
    /// the only delivery attempt, so it stays unset and the job retries
    /// hourly until a live token exists (bounded: completed/archived tasks
    /// drop out of the candidate query).
    /// </summary>
    public static bool ShouldWriteMarker(
        bool isDeadlineReminder, int deadlineReminderRepeat, int deliveredCount, int transientFailures)
    {
        if (transientFailures > 0)
        {
            return false;
        }

        if (deliveredCount > 0)
        {
            return true;
        }

        return isDeadlineReminder && deadlineReminderRepeat == 1;
    }

    private static bool IsMarkerStale(DateTime? lastSentAt, DateTime dueInstant)
    {
        return lastSentAt == null || lastSentAt.Value < dueInstant;
    }

    private static bool IsWeekday(DayOfWeek dayOfWeek)
    {
        return dayOfWeek != DayOfWeek.Saturday && dayOfWeek != DayOfWeek.Sunday;
    }
}
