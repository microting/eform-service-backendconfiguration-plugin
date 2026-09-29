using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Models;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Rebus.Handlers;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Messages;

namespace ServiceBackendConfigurationPlugin.Handlers;

public class MorningTourCaseCompletedHandler(
    eFormCore.Core sdkCore,
    ItemsPlanningDbContextHelper itemsPlanningDbContextHelper,
    BackendConfigurationDbContextHelper backendConfigurationDbContextHelper)
    : IHandleMessages<MorningTourCaseCompleted>
{
    public async Task Handle(MorningTourCaseCompleted message)
    {
        return;

        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        await using var
            itemsPlanningPnDbContext = itemsPlanningDbContextHelper.GetDbContext();
        await using var backendConfigurationPnDbContext =
            backendConfigurationDbContextHelper.GetDbContext();

        var dbCase = await sdkDbContext.Cases
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Id == message.CaseId) ??
                     await sdkDbContext.Cases
                         .FirstOrDefaultAsync(x => x.MicrotingCheckUid == message.CheckId);

        var planningCaseSite =
            await itemsPlanningPnDbContext.PlanningCaseSites.AsNoTracking()
                .FirstOrDefaultAsync(x => x.MicrotingSdkCaseId == dbCase.Id);

        if (planningCaseSite == null)
        {
            // var site = await sdkDbContext.Sites.FirstOrDefaultAsync(x => x.MicrotingUid == caseDto.SiteUId);
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
            Console.WriteLine($"planningCaseSite is null for CheckId: {message.CheckId}");
            return;
        }

        var planning =
            await itemsPlanningPnDbContext.Plannings.AsNoTracking()
                .FirstAsync(x => x.Id == planningCaseSite.PlanningId);

        Console.WriteLine("we have a morning tour");

        var planningCaseSites = await itemsPlanningPnDbContext.PlanningCaseSites
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.PlanningId == planningCaseSite.PlanningId).ToListAsync();

        var areaRulePlanning = await
            backendConfigurationPnDbContext.AreaRulePlannings.FirstOrDefaultAsync(x =>
                x.ItemPlanningId == planning.Id);
        var areaRule =
            await backendConfigurationPnDbContext.AreaRules.Where(x =>
                    x.Id == areaRulePlanning.AreaRuleId)
                .Include(x => x.Area)
                .Include(x => x.Property)
                .Include(x => x.AreaRuleTranslations)
                .FirstOrDefaultAsync();


        var planningSites = await itemsPlanningPnDbContext.PlanningSites
            .Where(x => x.PlanningId == planning.Id).ToListAsync();

        var sdkSite = await sdkDbContext.Sites.FirstAsync(x => x.Id == planningSites.First().SiteId);
        var language = await sdkDbContext.Languages.FirstAsync(x => x.Id == sdkSite.LanguageId);
        var caseIds = new List<int> {dbCase.Id};
        var fieldValues = await sdkCore.Advanced_FieldValueReadList(caseIds, language);
        // var chemicalDbContext = _chemicalDbContextHelper.GetDbContext();
        // var folder = await sdkDbContext.Folders.FirstAsync(x => x.Id == areaRule.FolderId);

        foreach (var caseSite in planningCaseSites)
        {

            var aseSite = await sdkDbContext.Cases.SingleAsync(x => x.Id == caseSite.MicrotingSdkCaseId).ConfigureAwait(false);
            await sdkCore.CaseDelete((int) aseSite.MicrotingUid!);
            var site = await sdkDbContext.Sites.FirstAsync(x => x.Id == caseSite.MicrotingSdkSiteId);
            var siteLanguage = await sdkDbContext.Languages.FirstAsync(x => x.Id == site.LanguageId);
            var mainElement = await sdkCore.ReadeForm(areaRule.SecondaryeFormId, siteLanguage);

            foreach (var fieldValue in fieldValues)
            {
                foreach (var element in mainElement.ElementList)
                {
                    var dataItemList = (DataElement) element;
                    var field = dataItemList.DataItemList
                        .FirstOrDefault(x => x.Id == fieldValue.FieldId);
                    if (field != null && !string.IsNullOrEmpty(fieldValue.ValueReadable))
                    {
                        if (fieldValue.ValueReadable == "unchecked")
                        {
                            fieldValue.ValueReadable = language.Name switch
                            {
                                "Danish" => "Ikke afkrydset",
                                "English" => "Not checked",
                                _ => "Nicht ausgewählt"
                            };
                        }
                        else if (fieldValue.ValueReadable == "checked")
                        {
                            fieldValue.ValueReadable = language.Name switch
                            {
                                "Danish" => "Afkrydset",
                                "English" => "Checked",
                                _ => "Ausgewählt"
                            };
                        }

                        field!.Description.InderValue += language.Name switch
                        {
                            "Danish" =>
                                $"<br>Sidst indsendte:<br><strong>{fieldValue.ValueReadable}</strong>",
                            "English" =>
                                $"<br>Last submitted:<br><strong>{fieldValue.ValueReadable}</strong>",
                            _ =>
                                $"<br>Zuletzt eingereicht:<br><strong>{fieldValue.ValueReadable}</strong>"
                        };
                    }
                }
            }

            await caseSite.Delete(itemsPlanningPnDbContext);
            var translation = itemsPlanningPnDbContext.PlanningNameTranslation
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.LanguageId == language.Id)
                .Where(x => x.PlanningId == planning.Id)
                .Select(x => x.Name)
                .FirstOrDefault();

            mainElement.Label = string.IsNullOrEmpty(planning.PlanningNumber)
                ? ""
                : planning.PlanningNumber;
            if (!string.IsNullOrEmpty(translation))
            {
                mainElement.Label +=
                    string.IsNullOrEmpty(mainElement.Label) ? $"{translation}" : $" - {translation}";
            }

            if (!string.IsNullOrEmpty(planning.BuildYear))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label)
                    ? $"{planning.BuildYear}"
                    : $" - {planning.BuildYear}";
            }

            if (!string.IsNullOrEmpty(planning.Type))
            {
                mainElement.Label += string.IsNullOrEmpty(mainElement.Label)
                    ? $"{planning.Type}"
                    : $" - {planning.Type}";
            }

            if (mainElement.ElementList.Count == 1)
            {
                mainElement.ElementList[0].Label = mainElement.Label;
            }

            var thisFolder = await sdkDbContext.Folders.SingleAsync(x => x.Id == areaRule.FolderId).ConfigureAwait(false);
            var folderMicrotingId = thisFolder.MicrotingUid.ToString();
            mainElement.CheckListFolderName = folderMicrotingId;
            mainElement.StartDate = DateTime.Now.ToUniversalTime();
            mainElement.EndDate = DateTime.Now.AddYears(10).ToUniversalTime();
            mainElement.Repeated = 1;
            var caseId = await sdkCore.CaseCreate(mainElement, "", (int)site!.MicrotingUid!, areaRule.FolderId).ConfigureAwait(false);
            var planningCase = new PlanningCase
            {
                PlanningId = planning.Id,
                Status = 66,
                MicrotingSdkeFormId = (int)areaRule.EformId!
            };
            await planningCase.Create(itemsPlanningPnDbContext).ConfigureAwait(false);
            var dbCaseId = await sdkDbContext.Cases.SingleAsync(x => x.MicrotingUid == caseId).ConfigureAwait(false);

            // var checkListSite = await sdkDbContext.CheckListSites.SingleOrDefaultAsync(x => x.MicrotingUid == caseId).ConfigureAwait(false);
            var newPlanningCaseSite = new PlanningCaseSite
            {
                MicrotingSdkSiteId = site.Id,
                MicrotingSdkeFormId = (int)areaRule.EformId!,
                Status = 66,
                PlanningId = planning.Id,
                PlanningCaseId = planningCase.Id,
                MicrotingSdkCaseId = dbCaseId.Id
                // MicrotingCheckListSitId = checkListSite.Id
            };

            await newPlanningCaseSite.Create(itemsPlanningPnDbContext).ConfigureAwait(false);
        }
    }
}