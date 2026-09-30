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
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Constants;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>Why a sync run did not apply everything.</summary>
public enum ChemicalSyncSkip
{
    /// <summary>Upserts and removals both applied.</summary>
    None,
    /// <summary>Non-2xx response: nothing applied.</summary>
    HttpFailure,
    /// <summary>The body held no list (<c>null</c>): nothing applied.</summary>
    NoFeed,
    /// <summary>The feed was <c>[]</c>: removals skipped.</summary>
    EmptyFeed,
    /// <summary>Too few distinct RemoteIds for this tenant: removals skipped.</summary>
    PartialFeed
}

/// <summary>What the 02:00 chemical register sync may apply this run.</summary>
public sealed record ChemicalSyncDecision(bool ApplyUpserts, bool ApplyRemovals, ChemicalSyncSkip Skip);

/// <summary>Result of one <see cref="ChemicalFeedGuard.RunAsync"/>; counts are null when not reached.</summary>
public sealed record ChemicalSyncOutcome(
    ChemicalSyncDecision Decision, HttpStatusCode StatusCode, int? FeedRows, int FeedRemoteIds, int? LocalActiveCount);

/// <summary>
/// Guards the 02:00 UTC "chemicalbase updates" step in <c>SearchListJob</c>.
/// A bad central feed (non-2xx status, no list, an empty list, or a list with
/// too few distinct RemoteIds) must never soft-delete the tenant's chemical
/// register. The whole sync is keyed on RemoteId; the central feed carries
/// no WorkflowState, so every feed chemical counts as live.
/// </summary>
public static class ChemicalFeedGuard
{
    /// <summary>
    /// A feed with fewer distinct RemoteIds than this share of the tenant's
    /// current non-removed chemicals is treated as partial: removals skipped.
    /// </summary>
    public const double MinimumFeedToLocalRatio = 0.5;

    /// <summary>The ratio check only applies from this many local chemicals.</summary>
    public const int RatioCheckMinimumLocalCount = 100;

    /// <summary>
    /// RemoteIds are matched in SQL by the upsert under the tenant DB's case-insensitive
    /// collation, so every in-memory comparison ignores case too (on trimmed values);
    /// otherwise a chemical the upsert matched could still be removed as "missing".
    /// </summary>
    public static readonly StringComparer RemoteIdComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly JsonSerializerOptions FeedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    /// <summary>
    /// Fetches and applies the feed. <paramref name="removeMissing"/> is only
    /// ever called after every upsert succeeded, and only when
    /// <see cref="Decide"/> allows removals. Exceptions propagate.
    /// </summary>
    /// <param name="countLocalActive">Non-removed local chemicals; called before any upsert.</param>
    /// <param name="upsertOne">Called in parallel once per distinct feed RemoteId (trimmed, case-insensitive); the chemical carries the trimmed RemoteId.</param>
    /// <param name="removeMissing">Receives the feed's full RemoteId set.</param>
    public static async Task<ChemicalSyncOutcome> RunAsync(
        Func<Task<HttpResponseMessage>> fetch,
        Func<Task<int>> countLocalActive,
        Func<Chemical, CancellationToken, ValueTask> upsertOne,
        Func<IReadOnlySet<string>, Task> removeMissing,
        ParallelOptions parallelOptions)
    {
        using var response = await fetch().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new ChemicalSyncOutcome(
                new ChemicalSyncDecision(false, false, ChemicalSyncSkip.HttpFailure), response.StatusCode, null, 0, null);
        }

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var feed = JsonSerializer.Deserialize<List<Chemical>>(body, FeedJsonOptions);
        if (feed == null)
        {
            return new ChemicalSyncOutcome(
                new ChemicalSyncDecision(false, false, ChemicalSyncSkip.NoFeed), response.StatusCode, null, 0, null);
        }

        // One chemical per RemoteId: rows without a key could only match local rows
        // by accident, and duplicates would race each other into duplicate creates.
        // Keys are trimmed and compared like the DB collation (see RemoteIdComparer).
        var keyed = feed
            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.RemoteId))
            .DistinctBy(x => x.RemoteId.Trim(), RemoteIdComparer)
            .ToList();
        foreach (var chemical in keyed)
        {
            chemical.RemoteId = chemical.RemoteId.Trim();
        }
        var localActiveCount = await countLocalActive().ConfigureAwait(false);
        var decision = Decide(feed.Count, keyed.Count, localActiveCount);

        if (decision.ApplyUpserts)
        {
            await Parallel.ForEachAsync(keyed, parallelOptions, upsertOne).ConfigureAwait(false);
        }

        if (decision.ApplyRemovals)
        {
            await removeMissing(keyed.Select(x => x.RemoteId).ToHashSet(RemoteIdComparer)).ConfigureAwait(false);
        }

        return new ChemicalSyncOutcome(decision, response.StatusCode, feed.Count, keyed.Count, localActiveCount);
    }

    /// <param name="feedRows">Parsed feed rows, or null when the body held no list.</param>
    /// <param name="feedRemoteIds">Distinct non-empty RemoteIds in the feed.</param>
    /// <param name="localActiveCount">Non-removed local chemicals, counted before any upsert.</param>
    public static ChemicalSyncDecision Decide(int? feedRows, int feedRemoteIds, int localActiveCount)
    {
        if (feedRows == null)
        {
            return new ChemicalSyncDecision(false, false, ChemicalSyncSkip.NoFeed);
        }

        if (feedRows == 0)
        {
            return new ChemicalSyncDecision(true, false, ChemicalSyncSkip.EmptyFeed);
        }

        if (feedRemoteIds == 0
            || (localActiveCount >= RatioCheckMinimumLocalCount
                && feedRemoteIds < localActiveCount * MinimumFeedToLocalRatio))
        {
            return new ChemicalSyncDecision(true, false, ChemicalSyncSkip.PartialFeed);
        }

        return new ChemicalSyncDecision(true, true, ChemicalSyncSkip.None);
    }

    /// <summary>
    /// Active local chemicals whose RemoteId is not in the feed. Rows without a
    /// RemoteId (local-only) are never removed. Matching is trimmed and
    /// case-insensitive whatever comparer <paramref name="feedRemoteIds"/> uses.
    /// </summary>
    public static List<Chemical> SelectToRemove(IReadOnlySet<string> feedRemoteIds, IEnumerable<Chemical> activeLocals)
    {
        var feedKeys = feedRemoteIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(RemoteIdComparer);
        return activeLocals
            .Where(x => !string.IsNullOrWhiteSpace(x.RemoteId) && !feedKeys.Contains(x.RemoteId.Trim()))
            .ToList();
    }

    /// <summary>
    /// The local row a feed chemical updates, from all local rows with its
    /// RemoteId: an active row wins, so a removed twin is never resurrected.
    /// </summary>
    public static Chemical PickLocalMatch(IReadOnlyCollection<Chemical> localRows)
    {
        return localRows.FirstOrDefault(x => x.WorkflowState != Constants.WorkflowStates.Removed)
               ?? localRows.FirstOrDefault();
    }

    /// <summary>
    /// Un-removes a local chemical whose RemoteId is in the feed again, so
    /// removal is never sticky.
    /// </summary>
    /// <returns>True when <paramref name="local"/> was restored.</returns>
    public static bool TryRestore(Chemical local)
    {
        if (local.WorkflowState != Constants.WorkflowStates.Removed)
        {
            return false;
        }

        local.WorkflowState = Constants.WorkflowStates.Created;
        return true;
    }
}
