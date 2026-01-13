/*
The MIT License (MIT)

Copyright (c) 2007 - 2022 Microting A/S

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

using Rebus.Bus;

namespace ServiceBackendConfigurationPlugin.Handlers;

using Infrastructure.Helpers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using Rebus.Handlers;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class EFormCompletedHandler(
    eFormCore.Core sdkCore,
    ItemsPlanningDbContextHelper itemsPlanningDbContextHelper,
    BackendConfigurationDbContextHelper backendConfigurationDbContextHelper,
    ChemicalDbContextHelper chemicalDbContextHelper,
    IBus bus)
    : IHandleMessages<eFormCompleted>
{
    private readonly ChemicalDbContextHelper _chemicalDbContextHelper = chemicalDbContextHelper;

    public async Task Handle(eFormCompleted message)
    {
        Console.WriteLine("info: EFormCompletedHandler .Handle called");
        Console.WriteLine($"info: message.CaseId: {message.CaseId}");
        Console.WriteLine($"info: message.MicrotingUId: {message.MicrotingUId}");
        Console.WriteLine($"info: message.CheckId: {message.CheckId}");
        Console.WriteLine($"info: message.SiteUId: {message.SiteUId}");
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        await using var
            itemsPlanningPnDbContext = itemsPlanningDbContextHelper.GetDbContext();
        await using var backendConfigurationPnDbContext =
            backendConfigurationDbContextHelper.GetDbContext();

        // var eformQuery = sdkDbContext.CheckListTranslations
        //     .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
        //     .AsQueryable();

        var eformIdForNewTasks = await sdkDbContext.CheckLists
            .Where(x => x.OriginalId == "142663new2")
            .Where(x => x.ParentId == null)
            .Select(x => x.Id)
            .FirstOrDefaultAsync();

        // var eformIdForNewTasksOld = await eformQuery
        //     .Where(x => x.Text == "01. New task")
        //     .Select(x => x.CheckListId)
        //     .FirstOrDefaultAsync();
        //
        // var eformIdForOngoingTasksOld = await eformQuery
        //     .Where(x => x.Text == "02. Ongoing task")
        //     .Select(x => x.CheckListId)
        //     .FirstOrDefaultAsync();

        var eformIdForOngoingTasks = await sdkDbContext.CheckLists
            .Where(x => x.OriginalId == "142664new2")
            .Where(x => x.ParentId == null)
            .Select(x => x.Id)
            .FirstOrDefaultAsync();

        if (eformIdForNewTasks == 0)
        {
            Console.WriteLine("info: eformIdForNewTasks is 0");
            return;
        }

        if (eformIdForOngoingTasks == 0)
        {
            Console.WriteLine("info: eformIdForOngoingTasks is 0");
            return;
        }

        // var eformIdForCompletedTasks = await eformQuery
        //     .Where(x => x.Text == "03. Completed task")
        //     .Select(x => x.CheckListId)
        //     .FirstOrDefaultAsync();
        //
        // if (eformIdForCompletedTasks == 0)
        // {
        //     Console.WriteLine("eformIdForCompletedTasks is 0");
        //     return;
        // }

        var eformIdForControlFloatingLayer = await sdkDbContext.CheckLists
            .Where(x => x.OriginalId == "142142new1")
            .Select(x => x.Id)
            .FirstOrDefaultAsync();

        // if (eformIdForControlFloatingLayer == 0)
        // {
        //     Console.WriteLine("eformIdForControlFloatingLayer is 0");
        //     return;
        // }

        var dbCase = await sdkDbContext.Cases
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Id == message.CaseId) ??
                     await sdkDbContext.Cases
                         .FirstOrDefaultAsync(x => x.MicrotingCheckUid == message.CheckId);
        if (dbCase == null)
        {
            Console.WriteLine("info: dbCase is null");
            return;
        }

        var workorderCase = await backendConfigurationPnDbContext.WorkorderCases
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.CaseId == message.MicrotingUId)
            .Include(x => x.ParentWorkorderCase)
            .Include(x => x.PropertyWorker)
            .ThenInclude(x => x.Property)
            .ThenInclude(x => x.PropertyWorkers)
            .ThenInclude(x => x.WorkorderCases)
            .FirstOrDefaultAsync();
        if (workorderCase != null)
        {
            Console.WriteLine($"info: found workorderCase: {workorderCase.Id}");
        }

        if (eformIdForNewTasks == dbCase.CheckListId && workorderCase != null)
        {
            await bus.SendLocal(new WorkOrderCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
                message.SiteUId));
            // WorkorderCaseCompletedHandler will handle this case
        }
        else if (eformIdForOngoingTasks == dbCase.CheckListId && workorderCase != null)
        {
            await bus.SendLocal(new WorkOrderCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
                message.SiteUId));
            // WorkorderCaseCompletedHandler will handle this case
        }
        // else if (eformIdForNewTasksOld == dbCase.CheckListId && workorderCase != null)
        // {
        //     await _bus.SendLocal(new OldWorkOrderCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
        //         message.SiteUId));
        //     // WorkorderCaseCompletedHandler will handle this case
        // }
        // else if (eformIdForOngoingTasksOld == dbCase.CheckListId && workorderCase != null)
        // {
        //     await _bus.SendLocal(new OldWorkOrderCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
        //         message.SiteUId));
        //     // WorkorderCaseCompletedHandler will handle this case
        // }
        else
        {
            var planningCaseSite =
                await itemsPlanningPnDbContext.PlanningCaseSites.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.MicrotingSdkCaseId == dbCase.Id);

            if (planningCaseSite == null)
            {
                var checkListSite = await sdkDbContext.CheckListSites.AsNoTracking().FirstOrDefaultAsync(x =>
                    x.MicrotingUid == message.MicrotingUId).ConfigureAwait(false);
                if (checkListSite == null)
                {
                    return;
                }
                planningCaseSite =
                    await itemsPlanningPnDbContext.PlanningCaseSites.AsNoTracking().FirstOrDefaultAsync(x =>
                        x.MicrotingCheckListSitId == checkListSite.Id).ConfigureAwait(false);
            }

            if (planningCaseSite == null)
            {
                Console.WriteLine($"info: planningCaseSite is null for CheckId: {message.CheckId}");
                return;
            }

            var planning =
                await itemsPlanningPnDbContext.Plannings.AsNoTracking()
                    .FirstAsync(x => x.Id == planningCaseSite.PlanningId);

            if (planning.RepeatType == RepeatType.Day && planning.RepeatEvery == 0)
            {
                var areaRulePlanning = await
                    backendConfigurationPnDbContext.AreaRulePlannings.FirstOrDefaultAsync(x =>
                        x.ItemPlanningId == planning.Id);
                if (areaRulePlanning == null)
                {
                    return;
                }
                var checkListTranslation = await sdkDbContext.CheckListTranslations.AsNoTracking().FirstAsync(x =>
                    x.Text == "25.01 Registrer produkter" && x.WorkflowState != Constants.WorkflowStates.Removed);
                var areaRule =
                    await backendConfigurationPnDbContext.AreaRules.Where(x =>
                            x.Id == areaRulePlanning.AreaRuleId)
                        .Include(x => x.Area)
                        .Include(x => x.Property)
                        .Include(x => x.AreaRuleTranslations)
                        .FirstOrDefaultAsync();
                if (areaRule == null)
                {
                    return;
                }
                if (planningCaseSite.MicrotingSdkeFormId == checkListTranslation.CheckListId)
                {
                    // ChemicalCaseCompletedHandler will handle this case
                    await bus.SendLocal(new ChemicalCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
                        message.SiteUId));
                }
                else
                {
                    checkListTranslation = await sdkDbContext.CheckListTranslations.AsNoTracking().FirstAsync(x =>
                        x.Text == "25.02 Vis kemisk produkt");
                    if (planningCaseSite.MicrotingSdkeFormId == checkListTranslation.CheckListId)
                    {
                        // ChemicalCaseCompletedHandler will handle this case
                        await bus.SendLocal(new ChemicalCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
                            message.SiteUId));
                    }
                    else
                    {
                        if (areaRule.SecondaryeFormId != 0 && (areaRule.SecondaryeFormName == "Morgenrundtur" || areaRule.SecondaryeFormName == "Morning tour"))
                        {
                            // Disabling this for now, as it is not used
                            // MorningTourCaseCompletedHandler will handle this case
                            //await _bus.SendLocal(new MorningTourCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
                            //    message.SiteUId));
                        }
                    }
                }
            }
            else
            {
                // if (planning.RepeatType == RepeatType.Week && planning.RepeatEvery == 1)
                // {
                //     // PoolCaseCompletedHandler will handle this case
                //     await _bus.SendLocal(new PoolHourCaseCompleted(message.CaseId, message.MicrotingUId, message.CheckId,
                //         message.SiteUId));
                // }
                // else
                // {
                int count = 0;
                while (planningCaseSite.Status != 100 && count < 100)
                {
                    Thread.Sleep(1000);
                    Console.WriteLine($"info: Waiting for case {planningCaseSite.Id} to be completed");
                    planningCaseSite = itemsPlanningPnDbContext.PlanningCaseSites.AsNoTracking()
                        .First(x => x.Id == planningCaseSite.Id);
                    if (planningCaseSite.Status == 100)
                    {
                        planningCaseSite =
                            itemsPlanningPnDbContext.PlanningCaseSites.AsNoTracking()
                                .First(x => x.Id == planningCaseSite.Id);
                    }
                    count++;
                }
                if (planningCaseSite.Status != 100)
                {
                    Console.WriteLine($"info: planningCaseSite {planningCaseSite.Id} is not completed");
                    return;
                }

                Console.WriteLine($"info: planningCaseSite {planningCaseSite.Id} is completed");
                Thread.Sleep(10000);

                Console.WriteLine($"info: planning.NextExecutionTime: {planning.NextExecutionTime}");


                var deadline = ((DateTime) planning.NextExecutionTime);
                Console.WriteLine($"info: Deadline: {deadline}");
                // backendConfigurationPnDbContext.Database.Log = Console.Write;

                var oneCompliance =
                    await backendConfigurationPnDbContext.Compliances.FirstOrDefaultAsync(x =>
                        x.PlanningCaseSiteId == planningCaseSite.PlanningCaseId);

                if (oneCompliance == null)
                {
                    var complianceList = await backendConfigurationPnDbContext.Compliances
                    .Where(x => x.Deadline == new DateTime(deadline.Year, deadline.Month, deadline.Day, 0, 0, 0))
                    .AsNoTracking()
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.PlanningId == planningCaseSite.PlanningId).ToListAsync();

                    Console.WriteLine($"info: complianceList.Count: {complianceList.Count}");

                    foreach (var compliance in complianceList)
                    {

                        if (compliance != null)
                        {
                            var dbCompliance =
                                await backendConfigurationPnDbContext.Compliances.FirstAsync(
                                    x => x.Id == compliance.Id);
                            await dbCompliance.Delete(backendConfigurationPnDbContext);
                            Console.WriteLine($"info: Deleted compliance {compliance.Id}");
                        }
                    }
                }
                else
                {
                    await oneCompliance.Delete(backendConfigurationPnDbContext);
                    Console.WriteLine($"info: Deleted compliance {oneCompliance.Id}");
                }

                var areaRulePlanning = await backendConfigurationPnDbContext.AreaRulePlannings.AsNoTracking()
                    .Where(x => x.ItemPlanningId == planningCaseSite.PlanningId).FirstOrDefaultAsync();

                var property =
                    await backendConfigurationPnDbContext.Properties.FirstOrDefaultAsync(x =>
                        x.Id == areaRulePlanning.PropertyId);

                if (property == null)
                {
                    return;
                }

                var planningSite = await backendConfigurationPnDbContext.PlanningSites
                    .Where(x =>
                        x.WorkflowState != ChemicalsBase.Infrastructure.Constants.Constants.WorkflowStates.Removed)
                    .FirstAsync(x =>
                        x.SiteId == planningCaseSite.MicrotingSdkSiteId && x.AreaRulePlanningsId == areaRulePlanning.Id);

                planningSite.Status = 100;
                Console.WriteLine($"info: Updated planningSite {planningSite.Id} to completed");
                await planningSite.Update(backendConfigurationPnDbContext);
            }
        }
    }
}