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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;
using MySqlConnector;

namespace ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

/// <summary>
/// Runs <see cref="TaskReminderDeliverer"/> every <see cref="Interval"/> on its own timer, isolated from
/// the rest of the service:
/// <list type="bullet">
/// <item>Non-reentrant: a tick still running when the next is due makes that next tick a no-op (skipped,
/// not queued). A tick is bounded by <see cref="TickBudget"/> (every database and FCM call gets the
/// token; unhandled claimed rows are handed back).</item>
/// <item>Never throws: every exception is caught and logged, at most once per <see cref="LogInterval"/>
/// per kind of problem; the next tick simply runs again.</item>
/// <item>Credential: until the eForm app's service-account JSON (<see cref="ServiceAccountJsonKey"/> in
/// PluginConfigurationValues, the row the web host's sender uses) has been applied, a tick only checks
/// for it, at most every <see cref="CredentialRecheck"/>, and claims nothing: rows stay Pending. It is
/// picked up without a restart when it appears. Its value is never logged.</item>
/// <item>A missing TaskNotifications table (the web plugin has not migrated yet) is logged once and
/// re-checked every <see cref="MissingTableSleep"/>.</item>
/// <item>Idle cost: one indexed SELECT per tick (the claim), plus the lease reaper (two UPDATEs on the
/// State index) every <see cref="ReapEvery"/> and the retention once an hour.</item>
/// </list>
/// </summary>
public sealed class TaskReminderDeliveryLoop : IDisposable
{
    /// <summary>The configuration row of the eForm app's Firebase credential (shared with the web host's
    /// PushNotificationService; written out of band by the fleet script).</summary>
    public const string ServiceAccountJsonKey = "BackendConfigurationSettings:EformFirebaseServiceAccountJson";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TickBudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CredentialRecheck = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MissingTableSleep = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ReapEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RetentionEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetentionAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan LogInterval = TimeSpan.FromHours(1);
    public const int RetentionBatch = 1000;
    public const int RetentionMaxBatches = 5;

    /// <summary>What a tick did (for tests).</summary>
    public enum TickOutcome
    {
        Ran,
        SkippedOverrun,
        Sleeping,
        NoCredential,
        InvalidCredential,
        TableMissing,
        Failed
    }

    private readonly Func<CancellationToken, Task<BackendConfigurationPnDbContext>> _createContext;
    private readonly ITaskReminderSender _sender;
    private readonly TaskReminderDeliverer _deliverer;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _log;
    private readonly Dictionary<string, DateTime> _lastLogged = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer _timer;
    private int _running;
    private DateTime _sleepUntil = DateTime.MinValue;
    private DateTime _nextReap = DateTime.MinValue;
    private DateTime _nextRetention = DateTime.MinValue;
    private bool _credentialLogged;

    public TaskReminderDeliveryLoop(
        Func<CancellationToken, Task<BackendConfigurationPnDbContext>> createContext,
        ITaskReminderSender sender,
        Func<DateTime> utcNow = null,
        Action<string> log = null)
    {
        _createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log ?? Console.WriteLine;
        _deliverer = new TaskReminderDeliverer(createContext, sender, _utcNow, _log);
    }

    /// <summary>Ticks that found the previous one still running.</summary>
    public int SkippedTicks => Volatile.Read(ref _skippedTicks);

    private int _skippedTicks;

    public void Start()
    {
        _timer ??= new Timer(_ => _ = TickAsync(), null, Interval, Interval);
    }

    /// <summary>Stops the timer, cancels a running tick and waits up to <paramref name="wait"/> for it.</summary>
    public void Stop(TimeSpan wait)
    {
        _timer?.Dispose();
        _timer = null;
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var waited = Stopwatch.StartNew();
        while (Volatile.Read(ref _running) == 1 && waited.Elapsed < wait)
        {
            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(5));
        _stopping.Dispose();
    }

    /// <summary>One tick. Never throws.</summary>
    public async Task<TickOutcome> TickAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Interlocked.Increment(ref _skippedTicks);
            LogThrottled("overrun", "info: TaskReminderDeliveryLoop: a tick was skipped because the previous one is still running");
            return TickOutcome.SkippedOverrun;
        }

        var now = _utcNow();
        try
        {
            if (now < _sleepUntil)
            {
                return TickOutcome.Sleeping;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            budget.CancelAfter(TickBudget);
            var elapsed = Stopwatch.StartNew();

            if (!_sender.IsReady)
            {
                var credential = await ApplyCredentialAsync(budget.Token).ConfigureAwait(false);
                if (credential != TickOutcome.Ran)
                {
                    _sleepUntil = now + CredentialRecheck;
                    return credential;
                }
            }

            if (now >= _nextReap)
            {
                var reaped = await _deliverer.ReapAsync(budget.Token).ConfigureAwait(false);
                _nextReap = now + ReapEvery;
                if (reaped > 0)
                {
                    _log($"info: TaskReminderDeliveryLoop: {reaped} expired lease(s) returned to the queue");
                }
            }

            await _deliverer.RunOnceAsync(TickBudget - elapsed.Elapsed - TimeSpan.FromSeconds(15), budget.Token)
                .ConfigureAwait(false);

            if (now >= _nextRetention)
            {
                await RetainAsync(now, budget.Token).ConfigureAwait(false);
                _nextRetention = now + RetentionEvery;
            }

            Forget("table-missing");
            return TickOutcome.Ran;
        }
        catch (Exception e) when (IsMissingTable(e))
        {
            LogThrottled("table-missing",
                "info: TaskReminderDeliveryLoop: the TaskNotifications table does not exist yet (the web plugin has not migrated); checking again in a few minutes");
            _sleepUntil = now + MissingTableSleep;
            return TickOutcome.TableMissing;
        }
        catch (Exception e)
        {
            LogThrottled("tick-failed",
                $"warn: TaskReminderDeliveryLoop: a tick failed ({Describe(e)}); the next tick tries again");
            return TickOutcome.Failed;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<TickOutcome> ApplyCredentialAsync(CancellationToken cancellationToken)
    {
        string serviceAccountJson;
        await using (var db = await _createContext(cancellationToken).ConfigureAwait(false))
        {
            serviceAccountJson = await db.PluginConfigurationValues
                .AsNoTracking()
                .Where(x => x.Name == ServiceAccountJsonKey)
                .Select(x => x.Value)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            LogThrottled("credential",
                $"warn: TaskReminderDeliveryLoop: {ServiceAccountJsonKey} is not set; task reminders stay queued until it is");
            _credentialLogged = true;
            return TickOutcome.NoCredential;
        }

        try
        {
            _sender.Initialize(serviceAccountJson);
        }
        catch (Exception e)
        {
            // Only the exception type: a parser message could quote part of the secret.
            LogThrottled("credential",
                $"warn: TaskReminderDeliveryLoop: {ServiceAccountJsonKey} is not a valid service-account key ({e.GetType().Name}); task reminders stay queued");
            _credentialLogged = true;
            return TickOutcome.InvalidCredential;
        }

        if (_credentialLogged)
        {
            _log("info: TaskReminderDeliveryLoop: the eForm Firebase credential is set; delivering task reminders");
            _credentialLogged = false;
        }

        Forget("credential");
        return TickOutcome.Ran;
    }

    /// <summary>
    /// Physically deletes finished rows older than 30 days, in a few capped batches. The product owner
    /// approved hard deletion for this outbox table only (it is a log; a soft-deleted outbox would grow
    /// forever); the helper never deletes a live row.
    /// </summary>
    private async Task RetainAsync(DateTime now, CancellationToken cancellationToken)
    {
        await using var db = await _createContext(cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        for (var batch = 0; batch < RetentionMaxBatches; batch++)
        {
            var count = await TaskNotificationOutbox
                .DeleteOlderThanAsync(db, now - RetentionAge, RetentionBatch, cancellationToken)
                .ConfigureAwait(false);
            deleted += count;
            if (count < RetentionBatch)
            {
                break;
            }
        }

        if (deleted > 0)
        {
            _log($"info: TaskReminderDeliveryLoop: retention deleted {deleted} finished reminder row(s) older than {RetentionAge.TotalDays:0} days");
        }
    }

    private void LogThrottled(string kind, string message)
    {
        try
        {
            var now = _utcNow();
            lock (_lastLogged)
            {
                if (_lastLogged.TryGetValue(kind, out var last) && now - last < LogInterval)
                {
                    return;
                }

                _lastLogged[kind] = now;
            }

            _log(message);
        }
        catch
        {
            // Logging must never fail a tick.
        }
    }

    private void Forget(string kind)
    {
        lock (_lastLogged)
        {
            _lastLogged.Remove(kind);
        }
    }

    private static bool IsMissingTable(Exception e)
    {
        for (var current = e; current != null; current = current.InnerException)
        {
            if (current is MySqlException { ErrorCode: MySqlErrorCode.NoSuchTable })
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(Exception e)
    {
        for (var current = e; current != null; current = current.InnerException)
        {
            if (current is MySqlException mysql)
            {
                return $"{e.GetType().Name}, MySQL {mysql.ErrorCode}";
            }
        }

        return e.GetType().Name;
    }
}
