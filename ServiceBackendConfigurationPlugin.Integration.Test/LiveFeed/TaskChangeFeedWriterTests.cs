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

namespace ServiceBackendConfigurationPlugin.Integration.Test.LiveFeed;

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TaskChangeKinds = Microting.EformBackendConfigurationBase.Infrastructure.Const.TaskChangeKinds;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

[TestFixture]
public class TaskChangeFeedWriterTests : LiveFeedTestBase
{
    private static readonly DateTime Deadline = new(2026, 11, 21, 0, 0, 0);

    [Test]
    public async Task Append_WritesOneRowWithKindIdsDayKeyAndSource()
    {
        await Writer().AppendAsync("UnitTest", new TaskChangeHint(
            TaskChangeKinds.Changed, PropertyId: 42, AreaRulePlanningId: 7, Deadline: Deadline, ComplianceId: 900));

        var row = (await FeedRowsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Kind, Is.EqualTo(TaskChangeKinds.Changed));
            Assert.That(row.PropertyId, Is.EqualTo(42));
            Assert.That(row.AreaRulePlanningId, Is.EqualTo(7));
            Assert.That(row.PlanDayKey, Is.EqualTo("2026-11-21"));
            Assert.That(row.ComplianceId, Is.EqualTo(900));
            Assert.That(row.SdkSiteId, Is.Null);
            Assert.That(row.Source, Is.EqualTo("UnitTest"));
            Assert.That(Warnings, Is.Empty);
        });
    }

    [Test]
    public async Task Append_ResolvesTaskAndPropertyFromThePlanningId_PreferringTheLiveTask()
    {
        await using var db = DbHelper.GetDbContext();
        var property = await CreatePropertyAsync(db);
        var planningId = NextPlanningId();
        await CreateTaskAsync(db, property.Id, planningId, removed: true);
        var live = await CreateTaskAsync(db, property.Id, planningId);

        await Writer().AppendAsync("UnitTest", new TaskChangeHint(
            TaskChangeKinds.Changed, ItemPlanningId: planningId, Deadline: Deadline, ComplianceId: 5));

        var row = (await FeedRowsAsync()).Single();
        Assert.That(row.PropertyId, Is.EqualTo(property.Id));
        Assert.That(row.AreaRulePlanningId, Is.EqualTo(live.Id));
        Assert.That(row.PlanDayKey, Is.EqualTo("2026-11-21"));
    }

    [Test]
    public async Task Append_PropertyCannotBeDetermined_WritesNoRow_LogsDebug_DoesNotThrow()
    {
        await Writer().AppendAsync("UnitTest", new TaskChangeHint(
            TaskChangeKinds.Changed, ItemPlanningId: NextPlanningId(), Deadline: Deadline));

        Assert.That(await FeedRowsAsync(), Is.Empty);
        Assert.That(Debugs, Has.Count.EqualTo(1));
        Assert.That(Warnings, Is.Empty);
    }

    [Test]
    public async Task Append_IdenticalHints_WriteOneRow()
    {
        var hint = new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 3, AreaRulePlanningId: 4, Deadline: Deadline, ComplianceId: 5);

        await Writer().AppendAsync("UnitTest", [hint, hint with { }]);

        Assert.That(await FeedRowsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Append_MoreThanThresholdForOneProperty_WritesOnePropertyRow_OtherPropertiesKeepTheirRows()
    {
        var many = Enumerable.Range(1, TaskChangeFeedWriter.CoalesceAbovePerProperty + 1)
            .Select(i => new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 10, AreaRulePlanningId: i, Deadline: Deadline, ComplianceId: i));
        var few = Enumerable.Range(1, 2)
            .Select(i => new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 11, AreaRulePlanningId: i, Deadline: Deadline, ComplianceId: 100 + i));

        await Writer().AppendAsync("UnitTest", many.Concat(few).ToList());

        var rows = await FeedRowsAsync();
        var coalesced = rows.Where(x => x.PropertyId == 10).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(coalesced, Has.Count.EqualTo(1));
            Assert.That(coalesced[0].Kind, Is.EqualTo(TaskChangeKinds.Property));
            Assert.That(coalesced[0].AreaRulePlanningId, Is.Null);
            Assert.That(coalesced[0].PlanDayKey, Is.Null);
            Assert.That(coalesced[0].ComplianceId, Is.Null);
            Assert.That(rows.Count(x => x.PropertyId == 11 && x.Kind == TaskChangeKinds.Changed), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Append_ExactlyThresholdForOneProperty_KeepsEveryRow()
    {
        var hints = Enumerable.Range(1, TaskChangeFeedWriter.CoalesceAbovePerProperty)
            .Select(i => new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 12, AreaRulePlanningId: i, Deadline: Deadline, ComplianceId: i))
            .ToList();

        await Writer().AppendAsync("UnitTest", hints);

        Assert.That((await FeedRowsAsync()).Count(x => x.Kind == TaskChangeKinds.Changed), Is.EqualTo(TaskChangeFeedWriter.CoalesceAbovePerProperty));
    }

    [Test]
    public async Task Append_MissingTable_DoesNotThrow_WarnsOncePerHour_AndKeepsTrying()
    {
        var noTable = new BackendConfigurationDbContextHelper(
            LiveFeedDatabases.ConnectionString(LiveFeedDatabases.NoFeedTableDb));
        var attempts = 0;
        var writer = Writer(() =>
        {
            attempts++;
            return noTable.GetDbContext();
        });
        var hint = new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 1, AreaRulePlanningId: 2, Deadline: Deadline);

        await Assert.DoesNotThrowAsync(() => writer.AppendAsync("UnitTest", hint));
        await Assert.DoesNotThrowAsync(() => writer.AppendAsync("UnitTest", hint));
        Assert.That(Warnings, Has.Count.EqualTo(1), "a missing table must not flood the log");
        Assert.That(Warnings[0], Does.StartWith("warn: "));

        Now = Now.Add(TaskChangeFeedWriter.WarningInterval);
        await writer.AppendAsync("UnitTest", hint);

        Assert.That(Warnings, Has.Count.EqualTo(2), "one more warning after the quiet hour");
        Assert.That(attempts, Is.EqualTo(3), "every call tries again: the table appears once the web host migrates");
    }

    [Test]
    public async Task Append_ContextFactoryThrows_DoesNotThrow_Warns()
    {
        var writer = Writer(() => throw new InvalidOperationException("no database"));

        await Assert.DoesNotThrowAsync(() => writer.AppendAsync("UnitTest",
            new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 1)));
        Assert.That(Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Append_CallerContextWithUnsavedChangesAndOpenTransaction_IsNeitherSavedNorAFailure()
    {
        await using var caller = DbHelper.GetDbContext();
        await using var transaction = await caller.Database.BeginTransactionAsync();
        var unsaved = new Property { Name = "Unsaved", Address = "Nowhere", CHR = Guid.NewGuid().ToString("N")[..8] };
        caller.Properties.Add(unsaved);

        await Writer().AppendAsync("UnitTest", new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 77, Deadline: Deadline));

        Assert.Multiple(() =>
        {
            Assert.That(Warnings, Is.Empty);
            Assert.That(caller.ChangeTracker.HasChanges(), Is.True, "the caller's pending change is untouched");
            Assert.That(caller.Database.CurrentTransaction, Is.Not.Null, "the caller's transaction is untouched");
        });

        var rows = await FeedRowsAsync();
        Assert.That(rows.Single().PropertyId, Is.EqualTo(77), "the row is committed on its own, outside the caller's transaction");

        await transaction.RollbackAsync();
        await using var check = DbHelper.GetDbContext();
        Assert.That(await check.Properties.AnyAsync(x => x.CHR == unsaved.CHR), Is.False, "the writer saved nothing of the caller's");
    }

    // ---- SearchListJob hints (the job itself switches on the wall clock hour and calls the
    // eForm cloud, so it cannot run here; these run the shipped hint builders through the writer).

    [Test]
    public async Task OverdueMove_ManyCompliancesOfOneProperty_CoalesceToOnePropertyRow()
    {
        var hints = Enumerable.Range(1, 25)
            .Select(i => SearchListJob.OverdueMoveHint(
                new Compliance { Id = i, PropertyId = 20, PlanningId = 1000 + i, Deadline = Deadline }, areaRulePlanningId: i))
            .Append(SearchListJob.OverdueMoveHint(
                new Compliance { Id = 99, PropertyId = 21, PlanningId = 2000, Deadline = Deadline }, areaRulePlanningId: 5))
            .ToList();

        await Writer().AppendAsync(SearchListJob.OverdueMoveSource, hints);

        var rows = await FeedRowsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(rows.Single(x => x.PropertyId == 20).Kind, Is.EqualTo(TaskChangeKinds.Property));
            var single = rows.Single(x => x.PropertyId == 21);
            Assert.That(single.Kind, Is.EqualTo(TaskChangeKinds.Changed));
            Assert.That(single.AreaRulePlanningId, Is.EqualTo(5));
            Assert.That(single.PlanDayKey, Is.EqualTo("2026-11-21"));
            Assert.That(single.ComplianceId, Is.EqualTo(99));
            Assert.That(single.Source, Is.EqualTo(SearchListJob.OverdueMoveSource));
        });
    }

    [Test]
    public void ShowExpireDateSweep_HintsOnlyForTheDeletedPlannings()
    {
        var hints = SearchListJob.SweepHints(
        [
            PlanningSweepResult.Success(1, "a"),
            PlanningSweepResult.Failure(2, new InvalidOperationException("not deleted")),
            PlanningSweepResult.Success(3, "c")
        ]);

        Assert.That(hints, Is.EqualTo(new[] { SearchListJob.SweptPlanningHint(1), SearchListJob.SweptPlanningHint(3) }));
    }

    [Test]
    public async Task ShowExpireDateSweep_RemovedPlanning_WritesRemovedForItsTask_UnownedPlanningWritesNothing()
    {
        await using var db = DbHelper.GetDbContext();
        var property = await CreatePropertyAsync(db);
        var ownedPlanningId = NextPlanningId();
        var task = await CreateTaskAsync(db, property.Id, ownedPlanningId, removed: true);

        await Writer().AppendAsync(SearchListJob.ShowExpireDateSweepSource,
        [
            SearchListJob.SweptPlanningHint(ownedPlanningId),
            SearchListJob.SweptPlanningHint(NextPlanningId())
        ]);

        var row = (await FeedRowsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.Kind, Is.EqualTo(TaskChangeKinds.Removed));
            Assert.That(row.PropertyId, Is.EqualTo(property.Id));
            Assert.That(row.AreaRulePlanningId, Is.EqualTo(task.Id));
            Assert.That(row.PlanDayKey, Is.Null, "the whole task, not one day");
            Assert.That(Debugs, Has.Count.EqualTo(1), "the unowned planning is skipped with one debug line");
        });
    }
}
