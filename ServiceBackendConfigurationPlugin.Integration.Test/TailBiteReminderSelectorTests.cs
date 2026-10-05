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

            var reminders = TailBiteReminderSelector.Select(actions.AsQueryable(), assessments.AsQueryable(),
                outbreaks.AsQueryable(), workers.AsQueryable(), Today);

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

            var reminders = TailBiteReminderSelector.Select(actions.AsQueryable(), assessments.AsQueryable(),
                outbreaks.AsQueryable(), workers.AsQueryable(), Today);

            Assert.That(reminders.Single().RecipientSiteIds, Is.EqualTo(new[] { 8 }));
        }

        [Test]
        public void OrphanPhotos_OnlyOldAndUnowned()
        {
            var linkedUuid = Guid.NewGuid();
            TailBiteRegistrationPhoto Photo(int id, Guid uuid, int ageDays, int siteId = 7, string state = Created) => new()
            {
                Id = id, RegistrationClientUuid = uuid, PropertyId = 1, UploadedBySiteId = siteId,
                CreatedAt = Today.AddDays(-ageDays), WorkflowState = state
            };
            var photos = new List<TailBiteRegistrationPhoto>
            {
                Photo(1, Guid.NewGuid(), 31),                                    // old orphan
                Photo(2, linkedUuid, 31),                                        // old, owned by registration 9
                Photo(3, Guid.NewGuid(), 5),                                     // recent orphan
                Photo(4, Guid.NewGuid(), 40, state: Constants.WorkflowStates.Removed),
                Photo(5, linkedUuid, 31, siteId: 8)                              // old, uuid of site 7's registration but uploaded by site 8
            };
            var registrations = new List<TailBiteRegistration>
            {
                new() { Id = 9, ClientUuid = linkedUuid, PropertyId = 1, SiteId = 7, WorkflowState = Created }
            };

            var orphans = TailBiteReminderSelector.OrphanPhotos(photos.AsQueryable(), registrations.AsQueryable(), Today).Select(p => p.Id).ToList();

            Assert.That(orphans, Is.EquivalentTo(new[] { 1, 5 }));
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
