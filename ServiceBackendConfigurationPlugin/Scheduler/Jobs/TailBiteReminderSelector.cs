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
using System.Collections.Generic;
using System.Linq;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;

namespace ServiceBackendConfigurationPlugin.Scheduler.Jobs;

public sealed record TailBiteReminder(
    int ActionId, int OutbreakId, int PropertyId, string Description, IReadOnlyList<int> RecipientSiteIds);

/// <summary>
/// Pure IQueryable compositions for <see cref="TailBiteDailyJob"/>: the job
/// runs them against EF, the tests against in-memory lists (this repo has no
/// database harness; see AdhocReminderRecipientTests for the LINQ-to-Objects
/// versus MariaDB caveats, notably case-sensitive string comparison).
/// </summary>
public static class TailBiteReminderSelector
{
    public static readonly TimeSpan OrphanAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Due = FollowUpDate before tomorrow (UTC date), action not done, not
    /// withdrawn, not removed, outbreak open and not removed, not reminded
    /// today. Recipients = the responsible site (only while a non-removed worker of the property) plus all
    /// non-removed managers of the property, distinct. PropertyWorker.WorkerId is an SDK site id.
    /// </summary>
    public static List<TailBiteReminder> Select(
        IQueryable<TailBiteAssessmentAction> actions, IQueryable<TailBiteRiskAssessment> assessments,
        IQueryable<TailBiteOutbreak> outbreaks, IQueryable<PropertyWorker> propertyWorkers, DateTime todayUtc)
    {
        var today = todayUtc.Date;
        var tomorrow = today.AddDays(1);
        var due = (from a in actions
                   join ra in assessments on a.AssessmentId equals ra.Id
                   join o in outbreaks on ra.OutbreakId equals o.Id
                   where ra.WorkflowState != Constants.WorkflowStates.Removed
                         && a.FollowUpDate < tomorrow && a.DoneAt == null && a.WithdrawnAt == null
                         && a.WorkflowState != Constants.WorkflowStates.Removed
                         && o.ClosedAt == null && o.WorkflowState != Constants.WorkflowStates.Removed
                         && (a.LastReminderAt == null || a.LastReminderAt < today)
                   select new { a.Id, OutbreakId = o.Id, o.PropertyId, a.Description, a.ResponsibleSiteId }).ToList();
        var propertyIds = due.Select(d => d.PropertyId).Distinct().ToList();
        var workers = propertyWorkers
            .Where(pw => propertyIds.Contains(pw.PropertyId) && pw.WorkflowState != Constants.WorkflowStates.Removed)
            .Select(pw => new { pw.PropertyId, pw.WorkerId, pw.TailBiteManager }).ToList();
        return due.Select(d =>
        {
            var onProperty = workers.Where(w => w.PropertyId == d.PropertyId).ToList();
            // The responsible site only counts while it is still a worker on the property.
            var recipients = onProperty.Where(w => w.TailBiteManager || w.WorkerId == d.ResponsibleSiteId)
                .Select(w => w.WorkerId).Distinct().ToList();
            return new TailBiteReminder(d.Id, d.OutbreakId, d.PropertyId, d.Description, recipients);
        }).ToList();
    }

    /// <summary>
    /// Orphan = not removed, older than <see cref="OrphanAge"/> and owned by no
    /// registration. Ownership needs uuid AND property AND uploading site to
    /// match (the plugin's TailBitePhotoOwnership rule).
    /// </summary>
    public static IQueryable<TailBiteRegistrationPhoto> OrphanPhotos(
        IQueryable<TailBiteRegistrationPhoto> photos, IQueryable<TailBiteRegistration> registrations, DateTime nowUtc)
    {
        var cutoff = nowUtc - OrphanAge;
        return photos.Where(p => p.CreatedAt < cutoff && p.WorkflowState != Constants.WorkflowStates.Removed
                                 && !registrations.Any(r => r.ClientUuid == p.RegistrationClientUuid
                                                            && r.PropertyId == p.PropertyId
                                                            && r.SiteId == p.UploadedBySiteId));
    }
}
