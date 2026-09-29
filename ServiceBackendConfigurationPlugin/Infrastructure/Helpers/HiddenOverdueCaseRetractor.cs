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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Constants;
using Microting.eFormApi.BasePn.Infrastructure.Helpers;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Sentry;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Retracts the device case of missed, uncompleted occurrences of tasks with
/// <c>ComplianceEnabled == false</c> ("Overskredet opgave vises ikke i app"),
/// issue #1325. Without this such a case stays live on the worker's device
/// forever.
///
/// Called daily from <c>SearchListJob</c> (hour 9). The selection has NO lower
/// date bound, so the first run after deploy IS the one-off cleanup of every
/// existing case; later runs only pick up the occurrences missed since.
///
/// Two kinds of case are retracted (see <see cref="SelectCasesToRetract"/>):
///  (a) MISSED — every case a compliance with a deadline before today ever
///      pointed at (current + <c>ComplianceVersions</c>). Deliberately no
///      filter on the compliance's WorkflowState: the web task tracker
///      soft-deleted many of these rows without retracting their cases.
///  (b) REVIVE ORPHAN — a live compliance for today or later that was
///      re-pointed at a newer case: the newer (current) case is kept, the
///      older case on the SAME site is retracted. An older case on another
///      site belongs to another worker and is left alone.
/// Only live, uncompleted SDK cases are retracted, and only through the SDK
/// Core API — Compliance rows are never modified.
/// </summary>
public class HiddenOverdueCaseRetractor
{
    private const int CompletedStatus = 100;
    private const int ChunkSize = 500;

    // CaseCreateLocalOnly allocates synthetic MicrotingUids from this offset up
    // (eform-sdk SqlController.NextSyntheticMicrotingUidAsync); the cloud never saw them.
    private const int LocalOnlyMicrotingUidOffset = 2_000_000_000;

    // Cloud deletes abandoned after the timeout keep running (Core.CaseDelete cannot be
    // cancelled); a later run must not start a second one for the same case.
    private static readonly ConcurrentDictionary<int, byte> CloudDeletesInFlight = new();

    // The run shares the hourly timer with the rest of SearchListJob; whatever
    // is left when the budget is spent is picked up by the next daily run.
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(20);

    // Core.CaseDelete answers "Parsing in progress" with a retry loop of
    // Thread.Sleep calls that can block for hours; see RetractCaseAsync.
    private static readonly TimeSpan CloudCaseDeleteTimeout = TimeSpan.FromSeconds(30);

    private readonly BackendConfigurationPnDbContext _backendConfigurationDbContext;
    private readonly MicrotingDbContext _sdkDbContext;
    private readonly eFormCore.Core _core;

    public HiddenOverdueCaseRetractor(
        BackendConfigurationPnDbContext backendConfigurationDbContext,
        MicrotingDbContext sdkDbContext,
        eFormCore.Core core)
    {
        _backendConfigurationDbContext = backendConfigurationDbContext;
        _sdkDbContext = sdkDbContext;
        _core = core;
    }

    /// <summary>One compliance and every SDK case id it has ever pointed at.</summary>
    public record ComplianceCaseRow(
        int ComplianceId,
        DateTime Deadline,
        string WorkflowState,
        int CurrentCaseId,
        IReadOnlyCollection<int> VersionCaseIds);

    public record SdkCaseRow(
        int Id,
        int? MicrotingUid,
        int? Status,
        DateTime? DoneAt,
        string WorkflowState,
        int? SiteId);

    public record RetractionCandidate(
        int ComplianceId,
        DateTime Deadline,
        SdkCaseRow Case,
        string Reason);

    /// <summary>
    /// The compliances of tasks whose missed occurrences are hidden from the
    /// app, in ANY WorkflowState. Several AreaRulePlannings can share one
    /// planning: it counts as hidden only when none of its live tasks reports
    /// missed occurrences, so a case of a compliance-tracked task is never
    /// retracted. Public so the tests run the shipped predicate rather than a
    /// copy of it.
    /// </summary>
    public static IQueryable<Compliance> SelectHiddenTaskCompliances(
        IQueryable<Compliance> compliances, IQueryable<AreaRulePlanning> areaRulePlannings)
    {
        var liveTasks = areaRulePlannings.Where(x => x.WorkflowState != Constants.WorkflowStates.Removed);
        return compliances.Where(c =>
            liveTasks.Any(x => x.ItemPlanningId == c.PlanningId && !x.ComplianceEnabled)
            && !liveTasks.Any(x => x.ItemPlanningId == c.PlanningId && x.ComplianceEnabled));
    }

    /// <summary>
    /// Pure selection over already-loaded rows. <paramref name="compliances"/>
    /// must already be limited to hidden-overdue tasks
    /// (<see cref="SelectHiddenTaskCompliances"/>); <paramref name="casesById"/>
    /// holds every SDK case the compliances reference that could be loaded.
    /// </summary>
    public static List<RetractionCandidate> SelectCasesToRetract(
        IReadOnlyCollection<ComplianceCaseRow> compliances,
        IReadOnlyDictionary<int, SdkCaseRow> casesById,
        DateTime todayUtc)
    {
        var today = todayUtc.Date;

        // The case a live, not yet missed occurrence currently points at is
        // on the device on purpose — never retract it, even when some other
        // row's history also references it.
        var keep = compliances
            .Where(c => c.Deadline.Date >= today && c.WorkflowState != Constants.WorkflowStates.Removed)
            .Select(c => c.CurrentCaseId)
            .ToHashSet();

        var result = new List<RetractionCandidate>();
        var seen = new HashSet<int>();

        foreach (var compliance in compliances.OrderBy(c => c.Deadline).ThenBy(c => c.ComplianceId))
        {
            IEnumerable<int> caseIds;
            string reason;
            if (compliance.Deadline.Date < today)
            {
                caseIds = compliance.VersionCaseIds.Append(compliance.CurrentCaseId);
                reason = "missed";
            }
            else if (compliance.WorkflowState != Constants.WorkflowStates.Removed
                     && casesById.TryGetValue(compliance.CurrentCaseId, out var currentCase)
                     && currentCase.SiteId.HasValue)
            {
                caseIds = compliance.VersionCaseIds
                    .Where(id => id != compliance.CurrentCaseId)
                    .Where(id => casesById.TryGetValue(id, out var oldCase)
                                 && oldCase.SiteId == currentCase.SiteId);
                reason = "revive orphan";
            }
            else
            {
                continue;
            }

            foreach (var caseId in caseIds)
            {
                if (caseId <= 0 || keep.Contains(caseId) || !seen.Add(caseId)
                    || !casesById.TryGetValue(caseId, out var sdkCase) || !IsLiveAndUncompleted(sdkCase))
                {
                    continue;
                }

                result.Add(new RetractionCandidate(compliance.ComplianceId, compliance.Deadline, sdkCase, reason));
            }
        }

        return result;
    }

    /// <summary>A case the cloud never saw: no MicrotingUid, or a synthetic one.</summary>
    public static bool IsLocalOnly(int? microtingUid)
    {
        return microtingUid is null or >= LocalOnlyMicrotingUidOffset;
    }

    private static bool IsLiveAndUncompleted(SdkCaseRow sdkCase)
    {
        return sdkCase.Status != CompletedStatus
               && sdkCase.DoneAt == null
               && sdkCase.WorkflowState != Constants.WorkflowStates.Removed
               && sdkCase.WorkflowState != Constants.WorkflowStates.Retracted;
    }

    /// <summary>Loads, selects and retracts. Returns the number of cases retracted.</summary>
    public async Task<int> RetractAsync(DateTime todayUtc)
    {
        var hiddenCompliances = SelectHiddenTaskCompliances(
            _backendConfigurationDbContext.Compliances,
            _backendConfigurationDbContext.AreaRulePlannings);

        var complianceRows = await hiddenCompliances
            .AsNoTracking()
            .Select(c => new { c.Id, c.Deadline, c.WorkflowState, c.MicrotingSdkCaseId })
            .ToListAsync();

        var versionRows = new List<(int ComplianceId, int MicrotingSdkCaseId)>();
        foreach (var chunk in complianceRows.Select(c => c.Id).Chunk(ChunkSize))
        {
            var rows = await _backendConfigurationDbContext.ComplianceVersions
                .AsNoTracking()
                .Where(v => v.MicrotingSdkCaseId > 0 && chunk.Contains(v.ComplianceId))
                .Select(v => new { v.ComplianceId, v.MicrotingSdkCaseId })
                .Distinct()
                .ToListAsync();
            versionRows.AddRange(rows.Select(v => (v.ComplianceId, v.MicrotingSdkCaseId)));
        }

        var versionCaseIdsByCompliance = versionRows
            .GroupBy(v => v.ComplianceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<int>)g.Select(v => v.MicrotingSdkCaseId).ToList());

        var compliances = complianceRows
            .Select(c => new ComplianceCaseRow(
                c.Id,
                c.Deadline,
                c.WorkflowState,
                c.MicrotingSdkCaseId,
                versionCaseIdsByCompliance.TryGetValue(c.Id, out var ids) ? ids : Array.Empty<int>()))
            .ToList();

        var allCaseIds = compliances
            .SelectMany(c => c.VersionCaseIds.Append(c.CurrentCaseId))
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var casesById = new Dictionary<int, SdkCaseRow>();
        foreach (var chunk in allCaseIds.Chunk(ChunkSize))
        {
            var cases = await _sdkDbContext.Cases
                .AsNoTracking()
                .Where(x => chunk.Contains(x.Id))
                .Select(x => new SdkCaseRow(x.Id, x.MicrotingUid, x.Status, x.DoneAt, x.WorkflowState, x.SiteId))
                .ToListAsync();
            foreach (var sdkCase in cases)
            {
                casesById[sdkCase.Id] = sdkCase;
            }
        }

        var candidates = SelectCasesToRetract(compliances, casesById, todayUtc);
        Log.LogEvent(
            $"info: HiddenOverdueCaseRetractor: {compliances.Count} compliances of hidden-overdue tasks reference {allCaseIds.Count} cases; {candidates.Count} live, uncompleted cases to retract");

        var retracted = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var candidate in candidates)
        {
            if (stopwatch.Elapsed > RunBudget)
            {
                Log.LogEvent(
                    $"info: HiddenOverdueCaseRetractor: run budget of {RunBudget.TotalMinutes} minutes spent; the rest is left for the next run");
                break;
            }

            try
            {
                await RetractCaseAsync(candidate.Case);
                retracted++;
                Console.WriteLine(
                    $"info: HiddenOverdueCaseRetractor: retracted case {candidate.Case.Id} (microtingUid {candidate.Case.MicrotingUid}, site {candidate.Case.SiteId}) of compliance {candidate.ComplianceId} with deadline {candidate.Deadline:yyyy-MM-dd} ({candidate.Reason})");
            }
            catch (Exception ex)
            {
                Log.LogException(
                    $"HiddenOverdueCaseRetractor: failed to retract case {candidate.Case.Id} of compliance {candidate.ComplianceId}: {ex.Message}");
                SentrySdk.CaptureException(ex);
            }
        }

        Log.LogEvent(
            $"info: HiddenOverdueCaseRetractor: retracted {retracted} of {candidates.Count} cases");
        return retracted;
    }

    /// <summary>
    /// Takes one case off the device through the SDK Core API.
    /// <para>A calendar case is created with <c>CaseCreateLocalOnly</c> and a synthetic
    /// MicrotingUid the cloud never saw, so it is removed locally with
    /// <c>Core.CaseDeleteResult</c> — no cloud call, nothing that can hang.</para>
    /// <para>A cloud-backed case is only removed when <c>Core.CaseDelete</c> confirms it
    /// (which also removes the local row). A timeout or failure throws, leaving the case
    /// for the next daily run rather than hiding it locally while it stays on the device.
    /// Core.CaseDelete cannot be cancelled: on "Parsing in progress" it keeps retrying in
    /// the background after the wait here is abandoned.</para>
    /// </summary>
    private async Task RetractCaseAsync(SdkCaseRow sdkCase)
    {
        if (IsLocalOnly(sdkCase.MicrotingUid))
        {
            if (!await _core.CaseDeleteResult(sdkCase.Id))
            {
                throw new InvalidOperationException($"Core.CaseDeleteResult returned false for case {sdkCase.Id}");
            }
            return;
        }

        var microtingUid = sdkCase.MicrotingUid!.Value;
        if (!CloudDeletesInFlight.TryAdd(microtingUid, 0))
        {
            throw new InvalidOperationException(
                $"cloud CaseDelete for case {sdkCase.Id} (microtingUid {microtingUid}) is still running from an earlier run; left for the next run");
        }

        var cloudDelete = _core.CaseDelete(microtingUid);
        _ = cloudDelete.ContinueWith(
            _ => CloudDeletesInFlight.TryRemove(microtingUid, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            if (!await cloudDelete.WaitAsync(CloudCaseDeleteTimeout))
            {
                throw new InvalidOperationException(
                    $"cloud CaseDelete for case {sdkCase.Id} (microtingUid {microtingUid}) was not confirmed; left for the next run");
            }
        }
        catch (TimeoutException) when (!cloudDelete.IsCompleted)
        {
            // The abandoned task must not fault unobserved.
            _ = cloudDelete.ContinueWith(
                t => Console.WriteLine(
                    $"warning: HiddenOverdueCaseRetractor: abandoned cloud CaseDelete for case {sdkCase.Id} faulted: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException(
                $"cloud CaseDelete for case {sdkCase.Id} (microtingUid {microtingUid}) did not answer within {CloudCaseDeleteTimeout.TotalSeconds}s; left for the next run");
        }
    }
}
