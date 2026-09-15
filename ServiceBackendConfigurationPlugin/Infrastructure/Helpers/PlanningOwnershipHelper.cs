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
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Ownership checks that span the items-planning database and the backend-configuration
/// database. The two live in <b>separate databases</b> behind separate DbContexts, so they
/// cannot be joined in a single LINQ query — every lookup here is deliberately a second,
/// batched round trip keyed on planning ids.
/// </summary>
public static class PlanningOwnershipHelper
{
    /// <summary>
    /// How many planning ids are sent per <c>IN (...)</c> lookup. Kept well under the
    /// MySQL/MariaDB placeholder ceiling so a large candidate set cannot blow up the query.
    /// </summary>
    public const int PlanningIdBatchSize = 500;

    /// <summary>
    /// Splits planning ids into batches of at most <paramref name="batchSize"/>.
    /// </summary>
    public static IEnumerable<List<int>> BatchPlanningIds(IReadOnlyCollection<int> planningIds,
        int batchSize = PlanningIdBatchSize)
    {
        if (planningIds == null)
        {
            throw new ArgumentNullException(nameof(planningIds));
        }

        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var batch = new List<int>(Math.Min(batchSize, planningIds.Count));
        foreach (var planningId in planningIds)
        {
            batch.Add(planningId);
            if (batch.Count != batchSize)
            {
                continue;
            }

            yield return batch;
            batch = new List<int>(batchSize);
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Returns the subset of <paramref name="candidates"/> that is <b>not</b> owned by a live
    /// <c>AreaRulePlanning</c>, using <paramref name="ownedPlanningIdLookupAsync"/> to resolve
    /// ownership one batch of ids at a time.
    /// </summary>
    /// <remarks>
    /// The lookup is injected so the exclusion can be exercised without a database.
    /// </remarks>
    public static async Task<List<Planning>> ExcludeBackendConfigurationOwnedAsync(
        IReadOnlyCollection<Planning> candidates,
        Func<List<int>, Task<List<int>>> ownedPlanningIdLookupAsync,
        int batchSize = PlanningIdBatchSize)
    {
        if (candidates == null)
        {
            throw new ArgumentNullException(nameof(candidates));
        }

        if (ownedPlanningIdLookupAsync == null)
        {
            throw new ArgumentNullException(nameof(ownedPlanningIdLookupAsync));
        }

        if (candidates.Count == 0)
        {
            return new List<Planning>();
        }

        var candidateIds = candidates.Select(x => x.Id).Distinct().ToList();

        var ownedPlanningIds = new HashSet<int>();
        foreach (var batch in BatchPlanningIds(candidateIds, batchSize))
        {
            var owned = await ownedPlanningIdLookupAsync(batch).ConfigureAwait(false);
            if (owned == null)
            {
                continue;
            }

            foreach (var planningId in owned)
            {
                ownedPlanningIds.Add(planningId);
            }
        }

        return candidates.Where(x => !ownedPlanningIds.Contains(x.Id)).ToList();
    }

    /// <summary>
    /// Database-bound overload: resolves ownership against the backend-configuration
    /// <c>AreaRulePlannings</c> table, ignoring rows that are already removed.
    /// </summary>
    public static Task<List<Planning>> ExcludeBackendConfigurationOwnedAsync(
        IReadOnlyCollection<Planning> candidates,
        BackendConfigurationPnDbContext backendConfigurationDbContext,
        int batchSize = PlanningIdBatchSize)
    {
        if (backendConfigurationDbContext == null)
        {
            throw new ArgumentNullException(nameof(backendConfigurationDbContext));
        }

        return ExcludeBackendConfigurationOwnedAsync(
            candidates,
            batch => backendConfigurationDbContext.AreaRulePlannings
                .AsNoTracking()
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => batch.Contains(x.ItemPlanningId))
                .Select(x => x.ItemPlanningId)
                .Distinct()
                .ToListAsync(),
            batchSize);
    }

    /// <summary>
    /// Processes plannings strictly one id at a time, so a failure on one planning cannot change
    /// the outcome of any other planning in the same run.
    /// </summary>
    /// <param name="planningIds">The plannings to process, identified by id only.</param>
    /// <param name="processOnePlanningAsync">
    /// Does the work for a single planning and returns the display name used in the diagnostic
    /// report. It is expected to fetch whatever entity it needs itself, so that nothing tracked in
    /// one iteration survives into the next.
    /// </param>
    /// <param name="resetBetweenPlannings">
    /// Discards any shared state the previous iteration left behind - in production
    /// <c>ItemsPlanningPnDbContext.ChangeTracker.Clear()</c>. This is what keeps one bad planning
    /// from cascading: <c>PnBase.Delete</c> calls <c>SaveChangesAsync</c> on the shared context, so
    /// a planning whose save was rejected stays tracked as Modified and would be bundled into - and
    /// break - the save of every later planning. Clearing is only safe because
    /// <paramref name="processOnePlanningAsync"/> re-fetches per id; no later iteration depends on
    /// an instance tracked by an earlier one. It runs after successes too, so the change tracker
    /// stays bounded over a large candidate set.
    /// </param>
    /// <returns>One result per id, in input order, each carrying either a name or the failure.</returns>
    public static async Task<List<PlanningSweepResult>> ProcessPlanningsIndividuallyAsync(
        IReadOnlyCollection<int> planningIds,
        Func<int, Task<string>> processOnePlanningAsync,
        Action resetBetweenPlannings)
    {
        if (planningIds == null)
        {
            throw new ArgumentNullException(nameof(planningIds));
        }

        if (processOnePlanningAsync == null)
        {
            throw new ArgumentNullException(nameof(processOnePlanningAsync));
        }

        if (resetBetweenPlannings == null)
        {
            throw new ArgumentNullException(nameof(resetBetweenPlannings));
        }

        var results = new List<PlanningSweepResult>(planningIds.Count);
        foreach (var planningId in planningIds)
        {
            PlanningSweepResult result;
            try
            {
                var planningName = await processOnePlanningAsync(planningId).ConfigureAwait(false);
                result = PlanningSweepResult.Success(planningId, planningName);
            }
            catch (Exception planningException)
            {
                result = PlanningSweepResult.Failure(planningId, planningException);
            }

            resetBetweenPlannings();
            results.Add(result);
        }

        return results;
    }
}

/// <summary>
/// The outcome of processing a single planning, so the caller can attribute every line of the
/// diagnostic report to the planning it actually belongs to.
/// </summary>
public sealed class PlanningSweepResult
{
    private PlanningSweepResult(int planningId, string planningName, Exception failure)
    {
        PlanningId = planningId;
        PlanningName = planningName;
        FailureException = failure;
    }

    public static PlanningSweepResult Success(int planningId, string planningName)
    {
        return new PlanningSweepResult(planningId, planningName, null);
    }

    public static PlanningSweepResult Failure(int planningId, Exception failure)
    {
        if (failure == null)
        {
            throw new ArgumentNullException(nameof(failure));
        }

        return new PlanningSweepResult(planningId, null, failure);
    }

    public int PlanningId { get; }

    /// <summary>The planning name, or <c>null</c> when the planning failed.</summary>
    public string PlanningName { get; }

    /// <summary>The exception this planning failed with, or <c>null</c> when it succeeded.</summary>
    public Exception FailureException { get; }

    public bool Succeeded => FailureException == null;
}
