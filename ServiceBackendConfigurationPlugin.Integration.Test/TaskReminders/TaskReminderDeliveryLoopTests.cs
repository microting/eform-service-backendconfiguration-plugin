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
namespace ServiceBackendConfigurationPlugin.Integration.Test.TaskReminders;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eFormApi.BasePn.Infrastructure.Database.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;
using Outcome = ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders.TaskReminderDeliveryLoop.TickOutcome;

[TestFixture]
public class TaskReminderDeliveryLoopTests : TaskReminderTestBase
{
    private const string Credential = "{\"type\":\"service_account\",\"secret\":\"SECRET-VALUE-1\"}";

    private static async Task SetCredentialAsync(string value)
    {
        await using var db = DbHelper.GetDbContext();
        db.PluginConfigurationValues.Add(new PluginConfigurationValue
        {
            Name = TaskReminderDeliveryLoop.ServiceAccountJsonKey, Value = value
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task MissingCredential_RowsStayPending_OneLogLine_RecoversWhenTheCredentialAppears()
    {
        Sender.Ready = false;
        await TokenAsync(31, "t");
        var row = await PlanAsync(startsIn: TimeSpan.FromMinutes(30));
        var loop = Loop();

        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.NoCredential));
        Now = Now.AddSeconds(2);
        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.Sleeping), "re-checked only every minute");
        Now = Now.AddSeconds(60);
        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.NoCredential));

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Pending), "nothing claimed");
        Assert.That(Sender.Calls, Is.Empty);
        Assert.That(Logs.Count(x => x.Contains("EformFirebaseServiceAccountJson")), Is.EqualTo(1), "one line, throttled");

        await SetCredentialAsync(Credential);
        Now = Now.AddSeconds(61);
        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.Ran));

        Assert.That(Sender.Initializations, Is.EqualTo(new[] { Credential }));
        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent), "picked up without a restart");
        Assert.That(Logs.Any(x => x.Contains("SECRET-VALUE-1")), Is.False, "the credential is never logged");
    }

    [Test]
    public async Task InvalidCredential_RowsStayPending_ValueNeverLogged()
    {
        Sender.Ready = false;
        await SetCredentialAsync("not-a-key SECRET-VALUE-2");
        var row = await PlanAsync();
        var loop = Loop();

        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.InvalidCredential));

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Pending));
        Assert.That(Logs, Has.Count.EqualTo(1));
        Assert.That(Logs.Any(x => x.Contains("SECRET-VALUE-2")), Is.False);
    }

    [Test]
    public async Task MissingTable_IsQuiet_OneLogLine_ThenSleeps()
    {
        var noTable = new TaskReminderContextSource(TaskReminderDatabases.ConnectionString(TaskReminderDatabases.NoTableDb));
        var loop = Loop(noTable.CreateAsync);

        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.TableMissing));
        Now = Now.AddSeconds(2);
        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.Sleeping));
        Now = Now + TaskReminderDeliveryLoop.MissingTableSleep;
        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.TableMissing));

        Assert.That(Logs, Has.Count.EqualTo(1), "a missing table does not flood the log");
        Assert.That(Logs[0], Does.StartWith("info: "));
    }

    [Test]
    public async Task Retention_DeletesOnlyOldFinishedRows()
    {
        var old = Now.AddDays(-31);
        var finished = new[]
        {
            TaskNotificationState.Sent, TaskNotificationState.Withdrawn, TaskNotificationState.Cancelled,
            TaskNotificationState.Expired, TaskNotificationState.Failed
        };
        var oldFinished = new long[finished.Length];
        for (var i = 0; i < finished.Length; i++)
        {
            oldFinished[i] = (await PlanAsync(eventId: 10 + i, startsIn: TimeSpan.FromDays(-31), createdAt: old)).Id;
        }

        var oldLive = (await PlanAsync(eventId: 20, startsIn: TimeSpan.FromDays(-31), createdAt: old)).Id;
        var recentFinished = (await PlanAsync(eventId: 21, startsIn: TimeSpan.FromDays(-1), createdAt: Now.AddDays(-1))).Id;
        await using (var db = DbHelper.GetDbContext())
        {
            for (var i = 0; i < finished.Length; i++)
            {
                var state = finished[i];
                var id = oldFinished[i];
                await db.TaskNotifications.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state));
            }

            await db.TaskNotifications.Where(x => x.Id == oldLive).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TaskNotificationState.WithdrawPending)
                .SetProperty(x => x.NextAttemptAtUtc, Now.AddYears(1)));
            await db.TaskNotifications.Where(x => x.Id == recentFinished).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, TaskNotificationState.Sent));
        }

        Assert.That(await Loop().TickAsync(), Is.EqualTo(Outcome.Ran));

        await using var check = DbHelper.GetDbContext();
        var left = await check.TaskNotifications.AsNoTracking().Select(x => x.Id).ToListAsync();
        Assert.That(left, Is.EquivalentTo(new[] { oldLive, recentFinished }));
    }

    [Test]
    public async Task ATickThatThrows_DoesNotStopTheLoop()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync();
        var calls = 0;
        var loop = Loop(ct => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("database down")
            : Contexts.CreateAsync(ct));

        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.Failed));
        Now = Now.AddSeconds(2);
        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.Ran));

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(Logs.Count(x => x.StartsWith("warn: TaskReminderDeliveryLoop: a tick failed")), Is.EqualTo(1));
    }

    [Test]
    public async Task AnOverrunningTick_MakesTheNextTickASkip_NotAQueue()
    {
        await TokenAsync(31, "t");
        await PlanAsync();
        var sending = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Sender.BeforeSend = async _ =>
        {
            sending.TrySetResult();
            await release.Task;
        };
        var loop = Loop();

        var first = loop.TickAsync();
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(await loop.TickAsync(), Is.EqualTo(Outcome.SkippedOverrun));
        Assert.That(loop.SkippedTicks, Is.EqualTo(1));

        release.SetResult();
        Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(30)), Is.EqualTo(Outcome.Ran));
        Assert.That(Sender.Calls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task StartAndStop_TheTimerDeliversAndStopsCleanly()
    {
        await TokenAsync(31, "t");
        var row = await PlanAsync();
        var loop = new TaskReminderDeliveryLoop(Contexts.CreateAsync, Sender, () => Now, Logs.Add);

        loop.Start();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while ((await RowAsync(row.Id)).State != TaskNotificationState.Sent && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(200);
        }

        loop.Stop(TimeSpan.FromSeconds(10));
        var callsAtStop = Sender.Calls.Count;
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.That((await RowAsync(row.Id)).State, Is.EqualTo(TaskNotificationState.Sent));
        Assert.That(Sender.Calls.Count, Is.EqualTo(callsAtStop), "no tick after Stop");
        loop.Dispose();
    }
}
