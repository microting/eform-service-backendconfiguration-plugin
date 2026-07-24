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

namespace ServiceBackendConfigurationPlugin.Integration.Test
{
    using System;
    using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

    [TestFixture]
    public class AdhocReminderEvaluatorTests
    {
        // 2026-07-20 is a Monday; the surrounding week gives us every
        // weekday/weekend combination the repeat logic cares about.
        private static readonly DateTime Monday = new(2026, 7, 20);
        private static readonly DateTime Friday = new(2026, 7, 24);
        private static readonly DateTime Saturday = new(2026, 7, 25);
        private static readonly DateTime Sunday = new(2026, 7, 26);

        private static AdhocTaskEntity VisibleReminderTask(
            DateTime visibleFrom, int timeMinutes = 480, DateTime? lastSentAt = null)
        {
            return new AdhocTaskEntity
            {
                VisibleReminder = true,
                VisibleFrom = visibleFrom,
                VisibleReminderTimeMinutes = timeMinutes,
                LastVisibleReminderSentAt = lastSentAt
            };
        }

        private static AdhocTaskEntity DeadlineReminderTask(
            DateTime deadline, int repeat = 0, int timeMinutes = 480, DateTime? lastSentAt = null)
        {
            return new AdhocTaskEntity
            {
                DeadlineReminder = true,
                Deadline = deadline,
                DeadlineReminderRepeat = repeat,
                DeadlineReminderTimeMinutes = timeMinutes,
                LastDeadlineReminderSentAt = lastSentAt
            };
        }

        // --- visible-from reminder -------------------------------------

        [Test]
        public void Visible_DueAtDefaultEightOClock()
        {
            var task = VisibleReminderTask(Monday);

            Assert.That(
                AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddHours(8)),
                Is.EqualTo(Monday.AddHours(8)));
        }

        [Test]
        public void Visible_NotDueBeforeReminderTime()
        {
            var task = VisibleReminderTask(Monday);

            Assert.That(
                AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddHours(7)),
                Is.Null);
        }

        [Test]
        public void Visible_NotDueWhenFlagOffOrDateMissing()
        {
            var flagOff = VisibleReminderTask(Monday);
            flagOff.VisibleReminder = false;
            var dateMissing = VisibleReminderTask(Monday);
            dateMissing.VisibleFrom = null;

            Assert.Multiple(() =>
            {
                Assert.That(
                    AdhocReminderEvaluator.DueVisibleReminderInstant(flagOff, Monday.AddHours(9)),
                    Is.Null);
                Assert.That(
                    AdhocReminderEvaluator.DueVisibleReminderInstant(dateMissing, Monday.AddHours(9)),
                    Is.Null);
            });
        }

        [Test]
        public void Visible_MarkerAtOrAfterInstantSuppresses()
        {
            var task = VisibleReminderTask(Monday, lastSentAt: Monday.AddHours(8).AddMinutes(5));

            Assert.That(
                AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddHours(9)),
                Is.Null);
        }

        [Test]
        public void Visible_StaleMarkerBeforeInstantStillFires()
        {
            // Marker from an earlier (edited-away) reminder date must not
            // suppress the current instant.
            var task = VisibleReminderTask(Monday, lastSentAt: Monday.AddDays(-3).AddHours(8));

            Assert.That(
                AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddHours(9)),
                Is.EqualTo(Monday.AddHours(8)));
        }

        [Test]
        public void Visible_CustomReminderTimeMinutesIsRespected()
        {
            var task = VisibleReminderTask(Monday, timeMinutes: 930); // 15:30

            Assert.Multiple(() =>
            {
                Assert.That(
                    AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddHours(15)),
                    Is.Null);
                Assert.That(
                    AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddHours(16)),
                    Is.EqualTo(Monday.AddMinutes(930)));
            });
        }

        [Test]
        public void Visible_TimeOfDayOnVisibleFromIsIgnored()
        {
            // The instant is the DATE at *ReminderTimeMinutes — an afternoon
            // timestamp on VisibleFrom must not push the instant.
            var task = VisibleReminderTask(Monday.AddHours(17));

            Assert.That(
                AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddHours(9)),
                Is.EqualTo(Monday.AddHours(8)));
        }

        [Test]
        public void Visible_LateCatchUpStillFiresDaysAfter()
        {
            var task = VisibleReminderTask(Monday);

            Assert.That(
                AdhocReminderEvaluator.DueVisibleReminderInstant(task, Monday.AddDays(4).AddHours(12)),
                Is.EqualTo(Monday.AddHours(8)));
        }

        // --- deadline reminder, fire once (Repeat == 0) -----------------

        [Test]
        public void DeadlineOnce_DueAtInstantAndSuppressedByMarker()
        {
            var due = DeadlineReminderTask(Friday);
            var sent = DeadlineReminderTask(Friday, lastSentAt: Friday.AddHours(8).AddMinutes(10));

            Assert.Multiple(() =>
            {
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(due, Friday.AddHours(8)),
                    Is.EqualTo(Friday.AddHours(8)));
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(sent, Friday.AddHours(9)),
                    Is.Null);
            });
        }

        [Test]
        public void DeadlineOnce_NotDueBeforeInstant()
        {
            var task = DeadlineReminderTask(Friday);

            Assert.That(
                AdhocReminderEvaluator.DueDeadlineReminderInstant(task, Friday.AddHours(7)),
                Is.Null);
        }

        [Test]
        public void DeadlineOnce_DoesNotRefireOnLaterDays()
        {
            var task = DeadlineReminderTask(Friday, lastSentAt: Friday.AddHours(8).AddMinutes(10));

            Assert.That(
                AdhocReminderEvaluator.DueDeadlineReminderInstant(task, Friday.AddDays(3).AddHours(9)),
                Is.Null);
        }

        [Test]
        public void DeadlineOnce_LateCatchUpStillFires()
        {
            var task = DeadlineReminderTask(Friday);

            Assert.That(
                AdhocReminderEvaluator.DueDeadlineReminderInstant(task, Sunday.AddHours(14)),
                Is.EqualTo(Friday.AddHours(8)));
        }

        [Test]
        public void Deadline_NotDueWhenFlagOffOrDateMissing()
        {
            var flagOff = DeadlineReminderTask(Friday);
            flagOff.DeadlineReminder = false;
            var dateMissing = DeadlineReminderTask(Friday);
            dateMissing.Deadline = null;

            Assert.Multiple(() =>
            {
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(flagOff, Friday.AddHours(9)),
                    Is.Null);
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(dateMissing, Friday.AddHours(9)),
                    Is.Null);
            });
        }

        // --- deadline reminder, weekday repeat (Repeat == 1) ------------

        [Test]
        public void DeadlineRepeat_FiresOnDeadlineDayEvenOnWeekend()
        {
            // The deadline-day fire mirrors Repeat == 0 (no weekday guard).
            var task = DeadlineReminderTask(Saturday, repeat: 1);

            Assert.That(
                AdhocReminderEvaluator.DueDeadlineReminderInstant(task, Saturday.AddHours(9)),
                Is.EqualTo(Saturday.AddHours(8)));
        }

        [Test]
        public void DeadlineRepeat_SkipsWeekendAfterDeadline()
        {
            var task = DeadlineReminderTask(Friday, repeat: 1, lastSentAt: Friday.AddHours(8).AddMinutes(10));

            Assert.Multiple(() =>
            {
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(task, Saturday.AddHours(9)),
                    Is.Null);
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(task, Sunday.AddHours(9)),
                    Is.Null);
            });
        }

        [Test]
        public void DeadlineRepeat_RefiresNextWeekday()
        {
            var task = DeadlineReminderTask(Friday, repeat: 1, lastSentAt: Friday.AddHours(8).AddMinutes(10));
            var nextMonday = Friday.AddDays(3);

            Assert.That(
                AdhocReminderEvaluator.DueDeadlineReminderInstant(task, nextMonday.AddHours(8)),
                Is.EqualTo(nextMonday.AddHours(8)));
        }

        [Test]
        public void DeadlineRepeat_OncePerDay()
        {
            var nextMonday = Friday.AddDays(3);
            var task = DeadlineReminderTask(
                Friday, repeat: 1, lastSentAt: nextMonday.AddHours(8).AddMinutes(10));

            Assert.Multiple(() =>
            {
                // Same day, later hour: suppressed by today's marker.
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(task, nextMonday.AddHours(15)),
                    Is.Null);
                // Next weekday: due again.
                Assert.That(
                    AdhocReminderEvaluator.DueDeadlineReminderInstant(task, nextMonday.AddDays(1).AddHours(9)),
                    Is.EqualTo(nextMonday.AddDays(1).AddHours(8)));
            });
        }

        [Test]
        public void DeadlineRepeat_NotDueBeforeTodaysSlot()
        {
            var task = DeadlineReminderTask(Friday, repeat: 1, lastSentAt: Friday.AddHours(8).AddMinutes(10));
            var nextMonday = Friday.AddDays(3);

            Assert.That(
                AdhocReminderEvaluator.DueDeadlineReminderInstant(task, nextMonday.AddHours(7)),
                Is.Null);
        }

        [Test]
        public void DeadlineRepeat_NotDueBeforeDeadline()
        {
            var task = DeadlineReminderTask(Friday, repeat: 1);

            Assert.That(
                AdhocReminderEvaluator.DueDeadlineReminderInstant(task, Monday.AddHours(9)),
                Is.Null);
        }
    }
}
