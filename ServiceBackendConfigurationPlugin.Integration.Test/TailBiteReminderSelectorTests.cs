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
    using System.Collections.Generic;
    using System.Linq;
    using FirebaseAdmin.Messaging;
    using Microting.eForm.Infrastructure.Constants;
    using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

    /// <summary>
    /// Runs the shipped TailBiteReminderSelector / TailBiteDailyJob queries over in-memory
    /// data (see AdhocReminderRecipientTests for why, and for the LINQ-to-Objects versus
    /// MariaDB caveats: EF translation and case-insensitive string collation are not covered).
    /// </summary>
    [TestFixture]
    public class TailBiteReminderSelectorTests
    {
        private static readonly DateTime Today = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        private const string Created = Constants.WorkflowStates.Created;

        private static TailBiteAssessmentAction Action(int id, int assessmentId, DateTime followUp, DateTime? done = null,
            DateTime? withdrawn = null, DateTime? reminded = null, string state = Created) => new()
        {
            Id = id, AssessmentId = assessmentId, Factor = TailBiteFactor.Climate, Description = $"Handling {id}",
            ResponsibleSiteId = 8, FollowUpDate = followUp, DoneAt = done, WithdrawnAt = withdrawn, LastReminderAt = reminded, WorkflowState = state
        };

        private static List<TailBiteProperty> Enabled(params int[] propertyIds) =>
            propertyIds.Select(id => new TailBiteProperty
                { Id = id, PropertyId = id, Enabled = true, WorkflowState = Created }).ToList();

        private static List<Property> Live(params int[] ids) =>
            ids.Select(id => new Property { Id = id, WorkflowState = Created }).ToList();

        /// <summary>Runs the shipped Select; defaults to property 1 being enabled and live.</summary>
        private static List<TailBiteReminder> Run(
            List<TailBiteAssessmentAction> actions, List<TailBiteRiskAssessment> assessments,
            List<TailBiteOutbreak> outbreaks, List<PropertyWorker> workers,
            List<TailBiteProperty> props = null, List<Property> live = null) =>
            TailBiteReminderSelector.Select(actions.AsQueryable(), assessments.AsQueryable(), outbreaks.AsQueryable(),
                workers.AsQueryable(), (props ?? Enabled(1)).AsQueryable(), (live ?? Live(1)).AsQueryable(), Today);

        /// <summary>One open outbreak, assessment and due action per property id (ids 1.. map to ids 100.. / 1..).</summary>
        private static (List<TailBiteOutbreak> Outbreaks, List<TailBiteRiskAssessment> Assessments,
            List<TailBiteAssessmentAction> Actions) Chain(params int[] propertyIds) =>
        (
            propertyIds.Select(id => new TailBiteOutbreak { Id = id, PropertyId = id, LocationId = 10 + id, WorkflowState = Created }).ToList(),
            propertyIds.Select(id => new TailBiteRiskAssessment { Id = id * 100, OutbreakId = id, WorkflowState = Created }).ToList(),
            propertyIds.Select(id => Action(id, id * 100, Today)).ToList()
        );

        [Test]
        public void Selects_DueOpenActions_WithResponsibleAndManagers_SkipsDoneWithdrawnClosedFutureAndRemindedToday()
        {
            var outbreaks = new List<TailBiteOutbreak>
            {
                new() { Id = 1, PropertyId = 1, LocationId = 10, WorkflowState = Created },                     // open
                new() { Id = 2, PropertyId = 1, LocationId = 11, ClosedAt = Today.AddDays(-1), WorkflowState = Created } // closed
            };
            var assessments = new List<TailBiteRiskAssessment>
            {
                new() { Id = 100, OutbreakId = 1, WorkflowState = Created },
                new() { Id = 200, OutbreakId = 2, WorkflowState = Created }
            };
            var actions = new List<TailBiteAssessmentAction>
            {
                Action(1, 100, Today.AddDays(-1)),                          // due, open -> selected
                Action(2, 100, Today.AddDays(1)),                           // not yet due
                Action(3, 100, Today.AddDays(-2), done: Today.AddDays(-1)), // done
                Action(4, 100, Today.AddDays(-2), withdrawn: Today),        // withdrawn
                Action(5, 100, Today.AddDays(-2), reminded: Today.AddHours(6)), // already reminded today
                Action(6, 200, Today.AddDays(-1)),                          // outbreak closed
                Action(7, 100, Today.AddDays(-2), state: Constants.WorkflowStates.Removed) // removed
            };
            var workers = new List<PropertyWorker>
            {
                new() { PropertyId = 1, WorkerId = 7, TailBiteManager = true, WorkflowState = Created },
                new() { PropertyId = 1, WorkerId = 8, TailBiteManager = false, WorkflowState = Created },
                new() { PropertyId = 1, WorkerId = 9, TailBiteManager = false, WorkflowState = Created },
                new() { PropertyId = 1, WorkerId = 6, TailBiteManager = true, WorkflowState = Constants.WorkflowStates.Removed }
            };

            var reminders = Run(actions, assessments, outbreaks, workers);

            Assert.That(reminders.Select(r => r.ActionId), Is.EqualTo(new[] { 1 }));
            Assert.That(reminders[0].RecipientSiteIds, Is.EquivalentTo(new[] { 7, 8 }));
        }

        [Test]
        public void Select_ResponsibleWhoIsAlsoManager_IsListedOnce()
        {
            var outbreaks = new List<TailBiteOutbreak> { new() { Id = 1, PropertyId = 1, LocationId = 10, WorkflowState = Created } };
            var assessments = new List<TailBiteRiskAssessment> { new() { Id = 100, OutbreakId = 1, WorkflowState = Created } };
            var actions = new List<TailBiteAssessmentAction> { Action(1, 100, Today) };
            var workers = new List<PropertyWorker>
            {
                new() { PropertyId = 1, WorkerId = 8, TailBiteManager = true, WorkflowState = Created }
            };

            var reminders = Run(actions, assessments, outbreaks, workers);

            Assert.That(reminders.Single().RecipientSiteIds, Is.EqualTo(new[] { 8 }));
        }

        [Test]
        public void Select_SkipsActionsOfRemovedAssessments()
        {
            var outbreaks = new List<TailBiteOutbreak> { new() { Id = 1, PropertyId = 1, LocationId = 10, WorkflowState = Created } };
            var assessments = new List<TailBiteRiskAssessment>
            {
                new() { Id = 100, OutbreakId = 1, WorkflowState = Constants.WorkflowStates.Removed }
            };
            var actions = new List<TailBiteAssessmentAction> { Action(1, 100, Today) };
            var workers = new List<PropertyWorker> { new() { PropertyId = 1, WorkerId = 8, WorkflowState = Created } };

            var reminders = Run(actions, assessments, outbreaks, workers);

            Assert.That(reminders, Is.Empty);
        }

        [Test]
        public void Select_RemovedResponsibleWorker_GetsNoReminder_ManagersStillDo()
        {
            var outbreaks = new List<TailBiteOutbreak> { new() { Id = 1, PropertyId = 1, LocationId = 10, WorkflowState = Created } };
            var assessments = new List<TailBiteRiskAssessment> { new() { Id = 100, OutbreakId = 1, WorkflowState = Created } };
            var actions = new List<TailBiteAssessmentAction> { Action(1, 100, Today) };
            var workers = new List<PropertyWorker>
            {
                new() { PropertyId = 1, WorkerId = 7, TailBiteManager = true, WorkflowState = Created },
                new() { PropertyId = 1, WorkerId = 8, TailBiteManager = false, WorkflowState = Constants.WorkflowStates.Removed }
            };

            var reminders = Run(actions, assessments, outbreaks, workers);

            Assert.That(reminders.Single().RecipientSiteIds, Is.EqualTo(new[] { 7 }));
        }

        [Test]
        public void Select_OnlyRecipientIsRemovedResponsible_YieldsNoRecipients()
        {
            var outbreaks = new List<TailBiteOutbreak> { new() { Id = 1, PropertyId = 1, LocationId = 10, WorkflowState = Created } };
            var assessments = new List<TailBiteRiskAssessment> { new() { Id = 100, OutbreakId = 1, WorkflowState = Created } };
            var actions = new List<TailBiteAssessmentAction> { Action(1, 100, Today) };
            var workers = new List<PropertyWorker>
            {
                new() { PropertyId = 1, WorkerId = 8, WorkflowState = Constants.WorkflowStates.Removed }
            };

            var reminders = Run(actions, assessments, outbreaks, workers);

            Assert.That(reminders.Single().RecipientSiteIds, Is.Empty);
        }

        [Test]
        public void Select_PausedMissingOrRemovedTailBiteProperty_GetsNoReminders_EnabledOtherPropertyUnaffected()
        {
            var (outbreaks, assessments, actions) = Chain(1, 2, 3, 4);
            var tailBiteProperties = new List<TailBiteProperty>
            {
                new() { Id = 1, PropertyId = 1, Enabled = true, WorkflowState = Created },
                new() { Id = 2, PropertyId = 2, Enabled = false, WorkflowState = Created },   // paused
                new() { Id = 3, PropertyId = 3, Enabled = true, WorkflowState = Constants.WorkflowStates.Removed } // removed row
                // property 4: no row at all
            };

            var reminders = Run(actions, assessments, outbreaks, new List<PropertyWorker>(),
                tailBiteProperties, Live(1, 2, 3, 4));

            Assert.That(reminders.Select(r => r.ActionId), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void Select_PausedThenReenabled_ResumesWithOneReminderPerAction()
        {
            var (outbreaks, assessments, actions) = Chain(1);
            actions[0].FollowUpDate = Today.AddDays(-7); // overdue for a week, never reminded while paused
            var workers = new List<PropertyWorker>();
            var property = new TailBiteProperty { Id = 1, PropertyId = 1, Enabled = false, WorkflowState = Created };
            var props = new List<TailBiteProperty> { property };

            Assert.That(Run(actions, assessments, outbreaks, workers, props), Is.Empty);

            property.Enabled = true;

            Assert.That(Run(actions, assessments, outbreaks, workers, props).Select(r => r.ActionId),
                Is.EqualTo(new[] { 1 })); // one, not a backlog

            actions[0].LastReminderAt = Today.AddHours(6); // sent today
            Assert.That(Run(actions, assessments, outbreaks, workers, props), Is.Empty);
        }

        [Test]
        public void Select_RemovedProperty_WithEnabledTailBite_GetsNoReminders()
        {
            var (outbreaks, assessments, actions) = Chain(1, 2);
            var properties = new List<Property>
            {
                new() { Id = 1, WorkflowState = Created },
                new() { Id = 2, WorkflowState = Constants.WorkflowStates.Removed }
            };

            var reminders = Run(actions, assessments, outbreaks, new List<PropertyWorker>(), Enabled(1, 2), properties);

            Assert.That(reminders.Select(r => r.ActionId), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void IsSystemicBatch_SameSystemicCodeEverywhere_IsSystemic()
        {
            Assert.That(TailBiteDailyJob.IsSystemicBatch(new List<MessagingErrorCode?>
                { MessagingErrorCode.InvalidArgument, MessagingErrorCode.InvalidArgument }), Is.True);
            Assert.That(TailBiteDailyJob.IsSystemicBatch(new List<MessagingErrorCode?>
                { MessagingErrorCode.SenderIdMismatch }), Is.True);
        }

        [Test]
        public void IsSystemicBatch_MixedSuccessUnregisteredOrEmpty_IsNotSystemic()
        {
            Assert.That(TailBiteDailyJob.IsSystemicBatch(new List<MessagingErrorCode?>
                { MessagingErrorCode.InvalidArgument, null }), Is.False);
            Assert.That(TailBiteDailyJob.IsSystemicBatch(new List<MessagingErrorCode?>
                { MessagingErrorCode.InvalidArgument, MessagingErrorCode.SenderIdMismatch }), Is.False);
            Assert.That(TailBiteDailyJob.IsSystemicBatch(new List<MessagingErrorCode?>
                { MessagingErrorCode.Unregistered, MessagingErrorCode.Unregistered }), Is.False);
            Assert.That(TailBiteDailyJob.IsSystemicBatch(new List<MessagingErrorCode?>()), Is.False);
        }

        [Test]
        public void OrphanPhotos_OnlyOldAndUnowned()
        {
            var linkedUuid = Guid.NewGuid();
            TailBiteRegistrationPhoto Photo(int id, Guid uuid, int ageDays, int siteId = 7, string state = Created) => new()
            {
                Id = id, RegistrationClientUuid = uuid, PropertyId = 1, UploadedBySiteId = siteId, SdkUploadedDataId = 100 + id,
                CreatedAt = Today.AddDays(-ageDays), WorkflowState = state
            };
            var photos = new List<TailBiteRegistrationPhoto>
            {
                Photo(1, Guid.NewGuid(), 31),                                    // old orphan
                Photo(2, linkedUuid, 31),                                        // old, owned by registration 9
                Photo(3, Guid.NewGuid(), 5),                                     // recent orphan
                Photo(4, Guid.NewGuid(), 40, state: Constants.WorkflowStates.Removed),
                Photo(5, linkedUuid, 31, siteId: 8),                             // old, uuid of site 7's registration but uploaded by site 8
                new() { Id = 6, RegistrationClientUuid = Guid.NewGuid(), PropertyId = 1, UploadedBySiteId = 7,
                        SdkUploadedDataId = 0, CreatedAt = Today.AddDays(-40), WorkflowState = Created } // old placeholder: not an orphan
            };
            var registrations = new List<TailBiteRegistration>
            {
                new() { Id = 9, ClientUuid = linkedUuid, PropertyId = 1, SiteId = 7, WorkflowState = Created }
            };

            var orphans = TailBiteReminderSelector.OrphanPhotos(photos.AsQueryable(), registrations.AsQueryable(), Today).Select(p => p.Id).ToList();

            Assert.That(orphans, Is.EquivalentTo(new[] { 1, 5 }));
        }

        [Test]
        public void StalePlaceholders_OnlyZeroDataIdOlderThanADay_NotRemoved()
        {
            TailBiteRegistrationPhoto Photo(int id, int dataId, double ageHours, string state = Created) => new()
            {
                Id = id, RegistrationClientUuid = Guid.NewGuid(), PropertyId = 1, UploadedBySiteId = 7,
                SdkUploadedDataId = dataId, CreatedAt = Today.AddHours(-ageHours), WorkflowState = state
            };
            var photos = new List<TailBiteRegistrationPhoto>
            {
                Photo(1, 0, 25),                                          // stale placeholder
                Photo(2, 0, 2),                                           // fresh placeholder (upload may be in flight)
                Photo(3, 55, 100),                                        // completed upload
                Photo(4, 0, 100, Constants.WorkflowStates.Removed)        // already removed
            };

            var stale = TailBiteReminderSelector.StalePlaceholders(photos.AsQueryable(), Today).Select(p => p.Id).ToList();

            Assert.That(stale, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void IsStillRemindable_OpenAction_IsTrue_DoneWithdrawnOrRemoved_IsFalse()
        {
            Assert.That(TailBiteReminderSelector.IsStillRemindable(Action(1, 100, Today)), Is.True);
            Assert.That(TailBiteReminderSelector.IsStillRemindable(Action(1, 100, Today, done: Today)), Is.False);
            Assert.That(TailBiteReminderSelector.IsStillRemindable(Action(1, 100, Today, withdrawn: Today)), Is.False);
            Assert.That(TailBiteReminderSelector.IsStillRemindable(
                Action(1, 100, Today, state: Constants.WorkflowStates.Removed)), Is.False);
        }

        [Test]
        public void SelectRecipientTokens_OnlyHalebidApp_OnlyThoseSites_NotRemoved()
        {
            DeviceToken Reg(int id, int siteId, string appId, string state = Created) => new()
            {
                Id = id, SdkSiteId = siteId, AppId = appId, FcmToken = $"fcm-{id}", WorkflowState = state
            };
            var registrations = new List<DeviceToken>
            {
                Reg(1, 7, TailBiteDailyJob.HalebidAppId),                                  // selected
                Reg(2, 7, "adhoc"),                                                        // other app
                Reg(3, 7, TailBiteDailyJob.HalebidAppId, Constants.WorkflowStates.Removed), // removed
                Reg(4, 9, TailBiteDailyJob.HalebidAppId),                                  // other site
                Reg(5, 8, TailBiteDailyJob.HalebidAppId)                                   // selected
            };

            var selected = TailBiteDailyJob.SelectRecipientTokens(registrations.AsQueryable(), new List<int> { 7, 8 })
                .Select(x => x.Id).ToList();

            Assert.That(selected, Is.EquivalentTo(new[] { 1, 5 }));
        }
    }
}
