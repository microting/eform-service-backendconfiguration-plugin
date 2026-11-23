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
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Helpers;
using TaskChangeKinds = Microting.EformBackendConfigurationBase.Infrastructure.Const.TaskChangeKinds;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Handlers;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Messages;
using BackendPlanningSite = Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.PlanningSite;
using SdkCase = Microting.eForm.Infrastructure.Data.Entities.Case;
using SdkCheckList = Microting.eForm.Infrastructure.Data.Entities.CheckList;

/// <summary>
/// Runs the two Rebus handlers that change the mobile task list end to end against real
/// databases (the SDK core is only used for its DbContextHelper on these paths).
/// </summary>
[TestFixture]
public class TaskChangeFeedHandlerTests : LiveFeedTestBase
{
    private const int SdkSiteId = 31;

    private static readonly ItemsPlanningDbContextHelper ItemsPlanningHelper =
        new(LiveFeedDatabases.ConnectionString(LiveFeedDatabases.ItemsPlanningDb));

    private static eFormCore.Core SdkCore() =>
        new() { DbContextHelper = new DbContextHelper(LiveFeedDatabases.ConnectionString(LiveFeedDatabases.SdkDb)) };

    private record Seed(Property Property, AreaRulePlanning Task, Planning Planning, PlanningCaseSite PlanningCaseSite, BackendPlanningSite PlanningSite);

    private static async Task<Seed> SeedTaskAsync(int sdkCaseId, int planningCaseSiteStatus, DateTime nextExecution)
    {
        await using var itemsPlanning = ItemsPlanningHelper.GetDbContext();
        var planning = new Planning
        {
            RepeatEvery = 1, RepeatType = RepeatType.Week, DayOfWeek = DayOfWeek.Monday, Enabled = true,
            NextExecutionTime = nextExecution, LastExecutedTime = nextExecution.AddDays(-7), StartDate = nextExecution.AddDays(-30),
            RelatedEFormId = 5, ShowExpireDate = true,
        };
        itemsPlanning.Plannings.Add(planning);
        await itemsPlanning.SaveChangesAsync();
        var planningCase = new PlanningCase { PlanningId = planning.Id, MicrotingSdkSiteId = SdkSiteId, MicrotingSdkCaseId = sdkCaseId };
        itemsPlanning.PlanningCases.Add(planningCase);
        await itemsPlanning.SaveChangesAsync();
        var planningCaseSite = new PlanningCaseSite
        {
            PlanningId = planning.Id, PlanningCaseId = planningCase.Id, MicrotingSdkSiteId = SdkSiteId,
            MicrotingSdkCaseId = sdkCaseId, Status = planningCaseSiteStatus,
        };
        itemsPlanning.PlanningCaseSites.Add(planningCaseSite);
        await itemsPlanning.SaveChangesAsync();

        await using var db = DbHelper.GetDbContext();
        var property = await CreatePropertyAsync(db);
        var task = await CreateTaskAsync(db, property.Id, planning.Id);
        var planningSite = new BackendPlanningSite
        {
            AreaRulePlanningsId = task.Id, SiteId = SdkSiteId, Status = 0, CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await planningSite.Create(db);
        return new Seed(property, task, planning, planningCaseSite, planningSite);
    }

    [Test]
    public async Task EformParsedByServer_CreatesTheCompliance_AndAppendsChangedForTheNewOccurrence()
    {
        var sdkCaseId = Random.Shared.Next(1_000_000, 2_000_000_000);
        var nextExecution = new DateTime(2026, 11, 30, 0, 0, 0);
        var seed = await SeedTaskAsync(sdkCaseId, planningCaseSiteStatus: 66, nextExecution);

        await new EformParsedByServerHandler(SdkCore(), ItemsPlanningHelper, DbHelper, Writer())
            .Handle(new EformParsedByServer(sdkCaseId, null, null));

        await using var db = DbHelper.GetDbContext();
        var compliance = await db.Compliances.AsNoTracking().SingleAsync(x => x.PlanningId == seed.Planning.Id);
        var planningSite = await db.PlanningSites.AsNoTracking().SingleAsync(x => x.Id == seed.PlanningSite.Id);
        var row = (await FeedRowsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(compliance.Deadline, Is.EqualTo(nextExecution), "existing outcome: the compliance is created");
            Assert.That(compliance.MicrotingSdkCaseId, Is.EqualTo(sdkCaseId));
            Assert.That(planningSite.Status, Is.EqualTo(70), "existing outcome: the planning site is marked parsed");
            Assert.That(row.Kind, Is.EqualTo(TaskChangeKinds.Changed));
            Assert.That(row.PropertyId, Is.EqualTo(seed.Property.Id));
            Assert.That(row.AreaRulePlanningId, Is.EqualTo(seed.Task.Id));
            Assert.That(row.PlanDayKey, Is.EqualTo("2026-11-30"));
            Assert.That(row.ComplianceId, Is.EqualTo(compliance.Id));
            Assert.That(row.Source, Is.EqualTo(EformParsedByServerHandler.FeedSource));
        });
    }

    [Test]
    public async Task EformParsedByServer_ComplianceAlreadyExists_AppendsNothing()
    {
        var sdkCaseId = Random.Shared.Next(1_000_000, 2_000_000_000);
        var nextExecution = new DateTime(2026, 12, 7, 0, 0, 0);
        var seed = await SeedTaskAsync(sdkCaseId, planningCaseSiteStatus: 66, nextExecution);
        await using (var db = DbHelper.GetDbContext())
        {
            await new Compliance
            {
                PropertyId = seed.Property.Id, PlanningId = seed.Planning.Id, Deadline = nextExecution,
                PlanningCaseSiteId = seed.PlanningCaseSite.PlanningCaseId, MicrotingSdkCaseId = sdkCaseId,
            }.Create(db);
        }

        await new EformParsedByServerHandler(SdkCore(), ItemsPlanningHelper, DbHelper, Writer())
            .Handle(new EformParsedByServer(sdkCaseId, null, null));

        Assert.That(await FeedRowsAsync(), Is.Empty, "nothing new appeared on the list");
    }

    /// <summary>Takes about 10 s: the handler sleeps 10 s once the case is completed.</summary>
    [Test]
    public async Task EFormCompleted_SoftDeletesTheCompliance_AndAppendsChangedForThatOccurrence()
    {
        int sdkCaseId;
        await using (var sdk = SdkCore().DbContextHelper.GetDbContext())
        {
            if (!await sdk.CheckLists.AnyAsync(x => x.OriginalId == "142663new2"))
            {
                sdk.CheckLists.Add(new SdkCheckList { OriginalId = "142663new2" });
                sdk.CheckLists.Add(new SdkCheckList { OriginalId = "142664new2" });
            }

            var taskForm = new SdkCheckList { OriginalId = "task-form" };
            sdk.CheckLists.Add(taskForm);
            await sdk.SaveChangesAsync();
            var sdkCase = new SdkCase { CheckListId = taskForm.Id, Status = 100, WorkflowState = Constants.WorkflowStates.Created };
            sdk.Cases.Add(sdkCase);
            await sdk.SaveChangesAsync();
            sdkCaseId = sdkCase.Id;
        }

        var deadline = new DateTime(2026, 11, 16, 0, 0, 0);
        var seed = await SeedTaskAsync(sdkCaseId, planningCaseSiteStatus: 100, deadline);
        Compliance compliance;
        await using (var db = DbHelper.GetDbContext())
        {
            compliance = new Compliance
            {
                PropertyId = seed.Property.Id, PlanningId = seed.Planning.Id, Deadline = deadline,
                PlanningCaseSiteId = seed.PlanningCaseSite.PlanningCaseId, MicrotingSdkCaseId = sdkCaseId,
            };
            await compliance.Create(db);
        }

        await new EFormCompletedHandler(SdkCore(), ItemsPlanningHelper, DbHelper, null, Writer())
            .Handle(new eFormCompleted(sdkCaseId, null, null, null));

        await using var check = DbHelper.GetDbContext();
        var after = await check.Compliances.AsNoTracking().SingleAsync(x => x.Id == compliance.Id);
        var planningSite = await check.PlanningSites.AsNoTracking().SingleAsync(x => x.Id == seed.PlanningSite.Id);
        var row = (await FeedRowsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(after.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Removed), "existing outcome: the compliance is soft-deleted");
            Assert.That(planningSite.Status, Is.EqualTo(100), "existing outcome: the planning site is completed");
            Assert.That(row.Kind, Is.EqualTo(TaskChangeKinds.Changed));
            Assert.That(row.PropertyId, Is.EqualTo(seed.Property.Id));
            Assert.That(row.AreaRulePlanningId, Is.EqualTo(seed.Task.Id));
            Assert.That(row.PlanDayKey, Is.Null, "a completed occurrence shows on its done date, not its deadline: ids only");
            Assert.That(row.ComplianceId, Is.EqualTo(compliance.Id));
            Assert.That(row.Source, Is.EqualTo(EFormCompletedHandler.FeedSource));
        });
    }
}
