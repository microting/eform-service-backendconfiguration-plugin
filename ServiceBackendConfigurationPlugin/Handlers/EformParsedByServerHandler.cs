/*
The MIT License (MIT)

Copyright (c) 2007 - 2020 Microting A/S

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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using Rebus.Handlers;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Messages;

namespace ServiceBackendConfigurationPlugin.Handlers;

public static class DateTimeExtensions
{
    public static DateTime StartOfWeek(this DateTime dt, DayOfWeek startOfWeek)
    {
        int diff = (7 + (dt.DayOfWeek - startOfWeek)) % 7;
        return dt.AddDays(-1 * diff).Date;
    }
}

public class EformParsedByServerHandler(
    eFormCore.Core sdkCore,
    ItemsPlanningDbContextHelper itemsPlanningDbContextHelper,
    BackendConfigurationDbContextHelper backendConfigurationDbContextHelper)
    : IHandleMessages<EformParsedByServer>
{
    public async Task Handle(EformParsedByServer message)
    {
        await using MicrotingDbContext sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        await using ItemsPlanningPnDbContext itemsPlanningPnDbContext = itemsPlanningDbContextHelper.GetDbContext();
        await using BackendConfigurationPnDbContext backendConfigurationPnDbContext = backendConfigurationDbContextHelper.GetDbContext();
        var planningCaseSite =
            await itemsPlanningPnDbContext.PlanningCaseSites
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.MicrotingSdkCaseId == message.CaseId);

        if (planningCaseSite == null)
        {
            // var site = await sdkDbContext.Sites.FirstOrDefaultAsync(x => x.MicrotingUid == caseDto.SiteUId);
            var checkListSite = await sdkDbContext.CheckListSites
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.MicrotingUid == message.MicrotingUId);
            if (checkListSite == null) return;
            planningCaseSite =
                await itemsPlanningPnDbContext.PlanningCaseSites
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.MicrotingCheckListSitId == checkListSite.Id);
        }

        if (planningCaseSite == null)
        {
            Console.WriteLine($"No planningCaseSite found for caseId : {message.CaseId}");
            return;
        }

        var areaRulePlanning = await backendConfigurationPnDbContext.AreaRulePlannings
            .Where(x => x.ItemPlanningId == planningCaseSite.PlanningId)
            .AsNoTracking()
            .FirstOrDefaultAsync();

        if (areaRulePlanning != null)
        {
            var property = await backendConfigurationPnDbContext.Properties.FirstAsync(x => x.Id == areaRulePlanning.PropertyId);

            var planning = await itemsPlanningPnDbContext.Plannings.AsNoTracking().FirstAsync(x =>
                x.Id == planningCaseSite.PlanningId);

            var planningSite = await backendConfigurationPnDbContext.PlanningSites
                .Where(x => x.WorkflowState != ChemicalsBase.Infrastructure.Constants.Constants.WorkflowStates.Removed)
                .FirstAsync(x => x.SiteId == planningCaseSite.MicrotingSdkSiteId && x.AreaRulePlanningsId == areaRulePlanning.Id);

            planningSite.Status = 70;
            await planningSite.Update(backendConfigurationPnDbContext);

            // if (!areaRulePlanning.ComplianceEnabled)
            // {
            //     Console.WriteLine($"Compliance not enabled for areaRulePlanning.Id : {areaRulePlanning.Id}");
            //     return;
            // }

            if (planning.RepeatEvery == 0 && planning.RepeatType == RepeatType.Day) { }
            else
            {
                Console.WriteLine($"Compliance is enabled for areaRulePlanning.Id : {areaRulePlanning.Id}");
                if (planning.NextExecutionTime == null)
                {
                    var now = DateTime.UtcNow;
                    if (planning.RepeatType == RepeatType.Day)
                    {
                        if (planning.RepeatEvery != 0)
                        {
                            var nextRun = now.AddDays(planning.RepeatEvery);
                            planning.NextExecutionTime = nextRun;
                            await planning.Update(itemsPlanningPnDbContext);
                        }
                    }
                    if (planning.RepeatType == RepeatType.Week)
                    {
                        if (planning.DayOfWeek != null)
                        {
                            var startOfWeek =
                                new DateTime(now.Year, now.Month, now.Day, 0, 0, 0).StartOfWeek(
                                    (DayOfWeek) planning.DayOfWeek);
                            var nextRun = startOfWeek.AddDays(planning.RepeatEvery * 7);
                            planning.NextExecutionTime = nextRun;
                            await planning.Update(itemsPlanningPnDbContext);
                        }
                    }

                    if (planning.RepeatType == RepeatType.Month)
                    {
                        if (planning.DayOfMonth != null)
                        {
                            if (planning.DayOfMonth == 0)
                            {
                                planning.DayOfMonth = 1;
                            }
                            var startOfMonth = new DateTime(now.Year, now.Month, (int) planning.DayOfMonth, 0, 0, 0);
                            var nextRun = startOfMonth.AddMonths(planning.RepeatEvery);
                            planning.NextExecutionTime = nextRun;
                            await planning.Update(itemsPlanningPnDbContext);
                        }
                    }
                }

                if (!backendConfigurationPnDbContext.Compliances.AsNoTracking().Any(x =>
                        x.PlanningCaseSiteId == planningCaseSite.PlanningCaseId &&
                        x.PlanningId == planningCaseSite.PlanningId
                        // &&
                        // x.PlanningCaseSiteId == planningCaseSite.Id &&
                        // x.WorkflowState != Constants.WorkflowStates.Removed
                        )
                    )
                {
                    Console.WriteLine($"We did not find a compliance for {planningCaseSite.PlanningId}, so we create one");
                    var deadLine = (DateTime)planning.NextExecutionTime!;
                    try
                    {
                        var compliance = new Compliance
                        {
                            PropertyId = property.Id,
                            PlanningId = planningCaseSite.PlanningId,
                            AreaId = areaRulePlanning.AreaId,
                            Deadline = new DateTime(deadLine.Year, deadLine.Month, deadLine.Day, 0, 0, 0),
                            StartDate = (DateTime)planning.LastExecutedTime!,
                            MicrotingSdkeFormId = planning.RelatedEFormId,
                            MicrotingSdkCaseId = (int) message.CaseId!,
                            PlanningCaseSiteId = planningCaseSite.PlanningCaseId
                        };

                        await compliance.Create(backendConfigurationPnDbContext);
                        Console.WriteLine("We created a compliance");

                    } catch (Exception ex)
                    {
                        // Code that will handle the situation where a compliance entry has already been created for a planning and deadline and db throws and duplicated key exception.
                        // That is completely fine and we just skip it, otherwise we throw the exception.
                        if (ex.InnerException is {HResult: -2147467259})
                        {
                            Console.WriteLine("We did not create a compliance, since it already exists");
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
                else
                {
                    var complianceEntry = await backendConfigurationPnDbContext.Compliances.FirstAsync(
                        x =>
                            x.Deadline == (DateTime) planning.NextExecutionTime &&
                            x.PlanningId == planningCaseSite.PlanningId
                        // &&
                        // x.PlanningCaseSiteId == planningCaseSite.Id &&
                        // x.WorkflowState != Constants.WorkflowStates.Removed
                    );
                    // complianceEntry.StartDate = (DateTime)planning.LastExecutedTime!;
                    // complianceEntry.MicrotingSdkCaseId = (int)message.CaseId!;
                    // complianceEntry.WorkflowState = Constants.WorkflowStates.Created;
                    // await complianceEntry.Update(backendConfigurationPnDbContext);
                }

                // var today = new DateTime(DateTime.Now.AddDays(1).Year, DateTime.Now.AddDays(1).Month, DateTime.Now.AddDays(1).Day, 0, 0, 0);
                //
                // if (backendConfigurationPnDbContext.Compliances.AsNoTracking().Any(x => x.Deadline < today && x.PropertyId == property.Id && x.WorkflowState != Constants.WorkflowStates.Removed))
                // {
                //     property.ComplianceStatus = 2;
                //     property.ComplianceStatusThirty = 2;
                //     await property.Update(backendConfigurationPnDbContext);
                // }
                // else
                // {
                //     if (!backendConfigurationPnDbContext.Compliances.AsNoTracking().Any(x =>
                //             x.Deadline < DateTime.UtcNow.AddDays(30) && x.PropertyId == property.Id &&
                //             x.WorkflowState != Constants.WorkflowStates.Removed))
                //     {
                //         property.ComplianceStatusThirty = 0;
                //         await property.Update(backendConfigurationPnDbContext);
                //     }
                //
                //     if (!backendConfigurationPnDbContext.Compliances.AsNoTracking().Any(x =>
                //             x.Deadline < DateTime.UtcNow && x.PropertyId == property.Id &&
                //             x.WorkflowState != Constants.WorkflowStates.Removed))
                //     {
                //         property.ComplianceStatus = 0;
                //         await property.Update(backendConfigurationPnDbContext);
                //     }
                // }
            }
        }
    }
}