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
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using TaskChangeKinds = Microting.EformBackendConfigurationBase.Infrastructure.Const.TaskChangeKinds;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;
using MySqlConnector;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// One "something changed" hint for the live-update feed, with whatever ids the call site already
/// has. <see cref="TaskChangeFeedWriter"/> fills in the rest: a missing task (AreaRulePlanning) id or
/// property id comes from <see cref="ItemPlanningId"/>; the plan day key is <see cref="Deadline"/>'s date.
/// </summary>
public sealed record TaskChangeHint(
    string Kind,
    int? PropertyId = null,
    int? AreaRulePlanningId = null,
    int? ItemPlanningId = null,
    DateTime? Deadline = null,
    int? ComplianceId = null);

/// <summary>
/// Appends <see cref="TaskChangeHint"/>s to the task change feed (TaskChangeFeedEntries) that the web
/// host reads and forwards to the live-update hub. Best effort: it never throws, and a failure never
/// changes the outcome of the job or handler that calls it.
/// <list type="bullet">
/// <item>Call it after the business save. It writes through its own short-lived context, so the
/// caller's pending changes and open transaction can neither be saved by it nor make it fail.</item>
/// <item>Each call is bounded by a 5 s budget: the server version is detected once, asynchronously and
/// cancellably, and cached (the shared context factory detects it synchronously on every context);
/// the connection open, the lookup and the inserts all honour the budget's token. No retrying
/// execution strategy: a retry would outlast the budget, and a lost hint only costs a refetch.</item>
/// <item>Every failure (e.g. the table is missing because the web plugin has not migrated yet) is
/// caught. The first is logged at Warning, then at most one Warning per <see cref="WarningInterval"/>;
/// every later call tries again.</item>
/// <item>More than <see cref="CoalesceAbovePerProperty"/> hints for one property in one call become one
/// <c>property</c> row.</item>
/// </list>
/// Thread safe; one instance per process (registered in Core).
/// </summary>
public sealed class TaskChangeFeedWriter
{
    /// <summary>Above this many distinct hints for one property in one call, one <c>property</c> row is written instead.</summary>
    public const int CoalesceAbovePerProperty = 20;

    /// <summary>The quiet time after a failure Warning.</summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromHours(1);

    // The longest a call may hold up its caller when the database is slow or unreachable.
    private static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(5);

    private readonly Func<CancellationToken, Task<BackendConfigurationPnDbContext>> _createContextAsync;
    private readonly string _connectionString;
    private DbContextOptions<BackendConfigurationPnDbContext> _options;
    private readonly Action<string> _warn;
    private readonly Action<string> _debug;
    private readonly Func<DateTime> _utcNow;
    private readonly object _warningGate = new();

    // One write at a time. A write holds the gate until it has really ended, also after its caller
    // stopped waiting for it, so at most one write is ever running past the budget.
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Task _abandonedWrite;
    private DateTime? _lastWarningAt;
    private int _suppressedFailures;

    /// <summary>The production writer, on the backend-configuration connection string.</summary>
    public TaskChangeFeedWriter(
        string connectionString,
        Action<string> warn = null,
        Action<string> debug = null,
        Func<DateTime> utcNow = null)
        : this(warn, debug, utcNow)
    {
        // The writer's own copy: connect and command timeouts within the budget, as a second guard
        // next to the cancellation token. Other code keeps its own settings.
        _connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            ConnectionTimeout = (uint)WriteBudget.TotalSeconds,
            DefaultCommandTimeout = (uint)WriteBudget.TotalSeconds
        }.ConnectionString;
        _createContextAsync = CreateContextFromCachedOptionsAsync;
    }

    /// <summary>The seams the tests use: the context source, the two log sinks and the clock.</summary>
    public TaskChangeFeedWriter(
        Func<BackendConfigurationPnDbContext> createContext,
        Action<string> warn = null,
        Action<string> debug = null,
        Func<DateTime> utcNow = null)
        : this(warn, debug, utcNow)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        _createContextAsync = _ => Task.FromResult(createContext());
    }

    private TaskChangeFeedWriter(Action<string> warn, Action<string> debug, Func<DateTime> utcNow)
    {
        _warn = warn ?? Console.WriteLine;
        _debug = debug ?? Console.WriteLine;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public Task AppendAsync(string source, TaskChangeHint hint) => AppendAsync(source, [hint]);

    /// <summary>
    /// Writes the hints, or gives up: the caller gets control back within the budget (a hard cutoff;
    /// token and timeouts alone are not one, since MySqlConnector allows a cancellation grace period
    /// after a command timeout). A write that outlives the budget is abandoned: it keeps running on its
    /// own, disposes its context, observes its own exceptions and ends through its timeouts. Until it
    /// has ended, new calls are skipped at once. Never throws.
    /// </summary>
    public async Task AppendAsync(string source, IReadOnlyCollection<TaskChangeHint> hints)
    {
        try
        {
            var pending = hints?.Where(x => x != null).ToList();
            if (pending is not { Count: > 0 })
            {
                return;
            }

            if (Volatile.Read(ref _abandonedWrite) is { IsCompleted: false })
            {
                ReportFailure(source, new TimeoutException(
                    "an earlier feed write is still running past its budget; this write is skipped"));
                return;
            }

            var deadline = Stopwatch.StartNew();
            if (!await _writeGate.WaitAsync(WriteBudget).ConfigureAwait(false))
            {
                ReportFailure(source, new TimeoutException("another feed write held the writer for the whole budget; this write is skipped"));
                return;
            }

            var remaining = WriteBudget - deadline.Elapsed;
            Task write;
            try
            {
                write = Task.Run(() => WriteAsync(source, pending, remaining));
            }
            catch
            {
                _writeGate.Release();
                throw;
            }

            if (remaining <= TimeSpan.Zero
                || await Task.WhenAny(write, Task.Delay(remaining)).ConfigureAwait(false) != write)
            {
                Volatile.Write(ref _abandonedWrite, write);
                ReportFailure(source, new TimeoutException(
                    $"the feed write did not finish within {WriteBudget.TotalSeconds:0} s; the caller stopped waiting and later writes are skipped until it has ended"));
            }
        }
        catch (Exception ex)
        {
            ReportFailure(source, ex);
        }
    }

    /// <summary>The write itself. Owns its context, releases the gate when it has ended, never faults.</summary>
    private async Task WriteAsync(string source, List<TaskChangeHint> pending, TimeSpan budgetLeft)
    {
        try
        {
            using var budget = new CancellationTokenSource(budgetLeft > TimeSpan.Zero ? budgetLeft : TimeSpan.Zero);
            await using var dbContext = await _createContextAsync(budget.Token).ConfigureAwait(false);
            var tasks = await LoadTasksAsync(dbContext, pending, budget.Token).ConfigureAwait(false);

            var unrouted = 0;
            var rows = new List<FeedRow>();
            foreach (var hint in pending)
            {
                var task = hint.ItemPlanningId is { } planningId && tasks.TryGetValue(planningId, out var t) ? t : default;
                var propertyId = hint.PropertyId is > 0 ? hint.PropertyId : task.PropertyId is > 0 ? task.PropertyId : null;
                if (propertyId == null)
                {
                    unrouted++;
                    continue;
                }

                rows.Add(new FeedRow(
                    hint.Kind,
                    propertyId.Value,
                    hint.AreaRulePlanningId ?? task.Id,
                    hint.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    hint.ComplianceId));
            }

            if (unrouted > 0)
            {
                _debug($"dbug: TaskChangeFeedWriter: {source}: skipped {unrouted} hint(s) whose property could not be determined");
            }

            foreach (var row in Coalesce(rows))
            {
                await TaskChangeFeed.AppendAndSaveAsync(
                    dbContext, row.Kind, row.PropertyId, row.AreaRulePlanningId, row.PlanDayKey, row.ComplianceId,
                    sdkSiteId: null, source: source, budget.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(source, ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// A context on options built once. The first call (and every call after a failed detection)
    /// reads the server version itself, cancellably; two racing first calls both build equivalent
    /// options, which is harmless.
    /// </summary>
    private async Task<BackendConfigurationPnDbContext> CreateContextFromCachedOptionsAsync(CancellationToken cancellationToken)
    {
        var options = Volatile.Read(ref _options);
        if (options == null)
        {
            options = await BuildOptionsAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _options, options);
        }

        return new BackendConfigurationPnDbContext(options);
    }

    private async Task<DbContextOptions<BackendConfigurationPnDbContext>> BuildOptionsAsync(CancellationToken cancellationToken)
    {
        string version;
        await using (var connection = new MySqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT VERSION()";
            version = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        // As BackendConfigurationPnContextFactory: a MariaDB server version, without its retry strategy.
        return new DbContextOptionsBuilder<BackendConfigurationPnDbContext>()
            .UseMySql(_connectionString, new MariaDbServerVersion(ServerVersion.Parse(version)))
            .Options;
    }

    private readonly record struct FeedRow(string Kind, int PropertyId, int? AreaRulePlanningId, string PlanDayKey, int? ComplianceId);

    private readonly record struct TaskIds(int? Id, int? PropertyId);

    /// <summary>Distinct rows; a property with more than <see cref="CoalesceAbovePerProperty"/> becomes one <c>property</c> row.</summary>
    private static IEnumerable<FeedRow> Coalesce(IEnumerable<FeedRow> rows)
    {
        foreach (var property in rows.Distinct().GroupBy(x => x.PropertyId))
        {
            var propertyRows = property.ToList();
            if (propertyRows.Count > CoalesceAbovePerProperty)
            {
                yield return new FeedRow(TaskChangeKinds.Property, property.Key, null, null, null);
                continue;
            }

            foreach (var row in propertyRows)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// The task (AreaRulePlanning) of each planning a hint still needs ids for, in batched indexed
    /// lookups on ItemPlanningId. Several tasks can share a planning: a live one wins, then the lowest id.
    /// </summary>
    private static async Task<Dictionary<int, TaskIds>> LoadTasksAsync(
        BackendConfigurationPnDbContext dbContext, List<TaskChangeHint> hints, CancellationToken cancellationToken)
    {
        var planningIds = hints
            .Where(x => x.ItemPlanningId is > 0 && (x.AreaRulePlanningId == null || x.PropertyId is not > 0))
            .Select(x => x.ItemPlanningId!.Value)
            .Distinct()
            .ToList();
        var result = new Dictionary<int, TaskIds>();
        foreach (var batch in PlanningOwnershipHelper.BatchPlanningIds(planningIds))
        {
            var tasks = await dbContext.AreaRulePlannings
                .AsNoTracking()
                .Where(x => batch.Contains(x.ItemPlanningId))
                .Select(x => new { x.Id, x.ItemPlanningId, x.PropertyId, Live = x.WorkflowState != Constants.WorkflowStates.Removed })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var task in tasks.OrderByDescending(x => x.Live).ThenBy(x => x.Id))
            {
                result.TryAdd(task.ItemPlanningId, new TaskIds(task.Id, task.PropertyId));
            }
        }

        return result;
    }

    private void ReportFailure(string source, Exception ex)
    {
        try
        {
            int suppressed;
            lock (_warningGate)
            {
                var now = _utcNow();
                if (_lastWarningAt is { } last && now - last < WarningInterval)
                {
                    _suppressedFailures++;
                    return;
                }

                _lastWarningAt = now;
                suppressed = _suppressedFailures;
                _suppressedFailures = 0;
            }

            var message = ex.InnerException == null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
            _warn($"warn: TaskChangeFeedWriter: {source}: could not append to the task change feed; live hints are skipped and clients fall back to their safety poll. {ex.GetType().Name}: {message}. {suppressed} failure(s) since the last warning; the next warning comes after {WarningInterval.TotalMinutes:0} minutes at the earliest.");
        }
        catch
        {
            // Logging must not fail the caller either.
        }
    }
}
