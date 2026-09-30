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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Constants;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>What the 02:00 chemical register sync may apply this run.</summary>
public sealed record ChemicalSyncDecision(
    bool ApplyUpserts, bool ApplyRemovals, string Reason, ChemicalSyncSkip Skip = ChemicalSyncSkip.None);

// RED stub (fix round 1): signatures only.
public enum ChemicalSyncSkip { None, HttpFailure, NoFeed, EmptyFeed, PartialFeed }

public sealed record ChemicalSyncOutcome(
    ChemicalSyncDecision Decision, int? FeedRows, int FeedRemoteIds, int? LocalActiveCount);

/// <summary>
/// Pure decision logic for the 02:00 UTC "chemicalbase updates" step in
/// <c>SearchListJob</c>. A bad central feed (non-2xx status, an empty list or
/// a partial list) must never soft-delete the tenant's chemical register.
/// </summary>
public static class ChemicalFeedGuard
{
    /// <summary>
    /// A feed smaller than this share of the tenant's current non-removed
    /// chemicals is treated as partial and the removal phase is skipped.
    /// </summary>
    public const double MinimumFeedToLocalRatio = 0.5;

    /// <summary>The ratio check only applies from this many local chemicals.</summary>
    public const int RatioCheckMinimumLocalCount = 100;

    /// <param name="feedCount">Parsed feed size, or null when the body held no list.</param>
    /// <param name="localActiveCount">Non-removed local chemicals, counted before any upsert.</param>
    public static ChemicalSyncDecision Decide(HttpStatusCode statusCode, int? feedCount, int localActiveCount)
    {
        var status = (int)statusCode;
        if (status is < 200 or > 299)
        {
            return new ChemicalSyncDecision(false, false,
                $"chemicalbase feed returned HTTP {status}; skipping the whole chemical sync");
        }

        if (feedCount == null)
        {
            return new ChemicalSyncDecision(false, false,
                "chemicalbase feed held no chemical list; skipping the whole chemical sync");
        }

        if (feedCount == 0)
        {
            return new ChemicalSyncDecision(true, false,
                $"chemicalbase feed is empty ({localActiveCount} local chemicals); skipping removals");
        }

        if (localActiveCount >= RatioCheckMinimumLocalCount
            && feedCount < localActiveCount * MinimumFeedToLocalRatio)
        {
            return new ChemicalSyncDecision(true, false,
                $"chemicalbase feed has {feedCount} chemicals, fewer than {MinimumFeedToLocalRatio:P0} of the {localActiveCount} local ones; skipping removals");
        }

        return new ChemicalSyncDecision(true, true, "feed accepted");
    }

    /// <summary>
    /// Ids of non-removed local chemicals whose RegistrationNo is not in the
    /// feed (the matching the sync has always used).
    /// </summary>
    public static List<int> SelectIdsToRemove(
        IReadOnlySet<string> feedRegistrationNos, IEnumerable<(int Id, string RegistrationNo)> activeLocals)
    {
        return activeLocals
            .Where(x => !feedRegistrationNos.Contains(x.RegistrationNo))
            .Select(x => x.Id)
            .ToList();
    }

    /// <summary>
    /// Un-removes a local chemical (matched on RemoteId) that is present and
    /// live in the feed again, so removal is never sticky.
    /// </summary>
    /// <returns>True when <paramref name="local"/> was restored.</returns>
    public static bool TryRestore(Chemical local, Chemical feedChemical)
    {
        if (local.WorkflowState != Constants.WorkflowStates.Removed
            || feedChemical.WorkflowState == Constants.WorkflowStates.Removed)
        {
            return false;
        }

        local.WorkflowState = Constants.WorkflowStates.Created;
        return true;
    }

    /// <summary>
    /// Runs <paramref name="processOne"/> for every feed chemical in parallel
    /// and returns every feed RegistrationNo. Collection is thread safe, so no
    /// entry is lost; any failure propagates, so callers never reach the
    /// removal phase after a partial pass.
    /// </summary>
    public static async Task<IReadOnlySet<string>> ProcessFeedAsync(
        IEnumerable<Chemical> feed,
        Func<Chemical, CancellationToken, ValueTask> processOne,
        ParallelOptions parallelOptions)
    {
        var registrationNos = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(feed, parallelOptions, async (chemical, ct) =>
        {
            registrationNos.Add(chemical.RegistrationNo);
            await processOne(chemical, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return new HashSet<string>(registrationNos);
    }

    public static ChemicalSyncDecision Decide(int? feedRows, int feedRemoteIds, int localActiveCount)
        => throw new NotImplementedException();

    public static List<Chemical> SelectToRemove(IReadOnlySet<string> feedRemoteIds, IEnumerable<Chemical> activeLocals)
        => throw new NotImplementedException();

    public static Chemical PickLocalMatch(IReadOnlyCollection<Chemical> localRows)
        => throw new NotImplementedException();

    public static bool TryRestore(Chemical local)
        => throw new NotImplementedException();

    public static Task<ChemicalSyncOutcome> RunAsync(
        Func<Task<HttpResponseMessage>> fetch,
        Func<Task<int>> countLocalActive,
        Func<Chemical, CancellationToken, ValueTask> upsertOne,
        Func<IReadOnlySet<string>, Task> removeMissing,
        ParallelOptions parallelOptions)
        => throw new NotImplementedException();
}
