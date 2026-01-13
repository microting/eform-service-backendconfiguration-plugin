using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Dto;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Models;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Rebus.Handlers;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Messages;

namespace ServiceBackendConfigurationPlugin.Handlers;

public class PoolHourCaseCompletedHandler(
    eFormCore.Core sdkCore,
    ItemsPlanningDbContextHelper itemsPlanningDbContextHelper,
    BackendConfigurationDbContextHelper backendConfigurationDbContextHelper)
    : IHandleMessages<PoolHourCaseCompleted>
{
    public async Task Handle(PoolHourCaseCompleted message)
    {
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
            Console.WriteLine($"info: planningCaseSite is null for CheckId: {message.CheckId}");
            return;
        }

        var planning =
            await itemsPlanningPnDbContext.Plannings.AsNoTracking()
                .FirstAsync(x => x.Id == planningCaseSite.PlanningId);

        var poolHour = await
            backendConfigurationPnDbContext.PoolHours.FirstOrDefaultAsync(x =>
                x.ItemsPlanningId == planning.Id);
        if (poolHour != null)
        {
            var poolPlanningCaseSite =
                await itemsPlanningPnDbContext.PlanningCaseSites.FirstOrDefaultAsync(x =>
                    x.MicrotingSdkCaseId == dbCase.Id);
            var theDate = new DateTime(dbCase.CreatedAt.Value.Year, dbCase.CreatedAt.Value.Month,
                poolPlanningCaseSite.CreatedAt.Day, poolHour.Index, 0, 0);
            var historyDate = new DateTime(dbCase.CreatedAt.Value.Year, dbCase.CreatedAt.Value.Month,
                poolPlanningCaseSite.CreatedAt.Day, 0, 0, 0);
            var poolHourResult =
                await backendConfigurationPnDbContext.PoolHourResults.FirstOrDefaultAsync(x =>
                    x.PoolHourId == poolHour.Id && x.Date == theDate);

            var checkList = await sdkDbContext.CheckListTranslations.FirstOrDefaultAsync(x =>
                x.Text == "00. Info boks");

            var areaRule =
                await backendConfigurationPnDbContext.AreaRules.Where(x =>
                        x.Id == poolHour.AreaRuleId)
                    .Include(x => x.AreaRuleTranslations)
                    .FirstOrDefaultAsync();

            if (poolHourResult == null)
            {

                var areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 0);
                var pulseFieldValue = await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 1);
                var phFieldValue = await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 2);
                var freeClorideFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 3);
                var tempFieldValue = await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 4);
                var numberOfGuestsFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 5);
                var clarityFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 6);
                var measuredFreeClorideFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 7);
                var totalClorideFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 8);
                var boundClorideFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 9);
                var measuredPhFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 10);
                var receiptFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 11);
                var measuredTempFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 12);
                var commentFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);
                areaField =
                    await sdkDbContext.Fields.FirstAsync(x =>
                        x.CheckListId == planning.RelatedEFormId + 1 && x.DisplayIndex == 13);
                var doneByFieldValue =
                    await sdkDbContext.FieldValues.FirstAsync(x =>
                        x.FieldId == areaField.Id && x.CaseId == dbCase.Id);

                poolHourResult = new PoolHourResult
                {
                    PoolHourId = poolHour.Id,
                    PlanningId = planning.Id,
                    FolderId = (int) planning.SdkFolderId,
                    Date = theDate,
                    PulseRateAtOpening = double.Parse((pulseFieldValue.Value ?? "0").Replace(",", ".")),
                    ReadPhValue = double.Parse((phFieldValue.Value ?? "0").Replace(",", ".")),
                    ReadFreeChlorine = double.Parse((freeClorideFieldValue.Value ?? "0").Replace(",", ".")),
                    ReadTemperature = double.Parse((tempFieldValue.Value ?? "0").Replace(",", ".")),
                    NumberOfGuestsAtClosing = double.Parse((numberOfGuestsFieldValue.Value ?? "0").Replace(",", ".")),
                    Clarity = clarityFieldValue.Value,
                    MeasuredFreeChlorine = double.Parse((measuredFreeClorideFieldValue.Value ?? "0").Replace(",", ".")),
                    MeasuredTotalChlorine = double.Parse((totalClorideFieldValue.Value ?? "0").Replace(",", ".")),
                    MeasuredBoundChlorine = double.Parse((boundClorideFieldValue.Value ?? "0").Replace(",", ".")),
                    MeasuredPh = double.Parse((measuredPhFieldValue.Value ?? "0").Replace(",", ".")),
                    AcknowledgmentOfPulseRateAtOpening = receiptFieldValue.Value,
                    MeasuredTempDuringTheDay = double.Parse((measuredTempFieldValue.Value ?? "0").Replace(",", ".")),
                    Comment = commentFieldValue.Value,
                    DoneByUserId = doneByFieldValue.Value == "null" ? 0 : int.Parse(doneByFieldValue.Value),
                    DoneByUserName = doneByFieldValue.Value,
                    SdkCaseId = dbCase.Id,
                    AreaRuleId = poolHour.AreaRuleId,
                    DoneAt = (DateTime) dbCase.DoneAt
                };
                await poolHourResult.Create(backendConfigurationPnDbContext);

                var planningSites = await itemsPlanningPnDbContext.PlanningSites
                    .Where(x => x.PlanningId == planning.Id).ToListAsync();

                var lookupName = areaRule.AreaRuleTranslations.First().Name;

                var subfolder = await sdkDbContext.Folders
                    .Include(x => x.FolderTranslations)
                    .Where(x => x.ParentId == areaRule.FolderId)
                    .Where(x => x.FolderTranslations.Any(y => y.Name == lookupName))
                    .FirstOrDefaultAsync();
                var innerLookupName = $"{(int) poolHour.DayOfWeek}. {poolHour.DayOfWeek.ToString().Substring(0, 3)}";
                var poolDayFolder = await sdkDbContext.Folders
                    .Include(x => x.FolderTranslations)
                    .Where(x => x.ParentId == subfolder.Id)
                    .Where(x => x.FolderTranslations.Any(y => y.Name == innerLookupName))
                    .FirstAsync();


                Regex regex = new Regex(@"(\d\.\s)");
                TimeZoneInfo timeZoneInfo;
                try
                {
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
                }
                catch
                {
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("E. Europe Standard Time");
                }

                foreach (var planningSite in planningSites)
                {
                    var poolHistorySite = await backendConfigurationPnDbContext.PoolHistorySites
                        .Where(x => x.AreaRuleId == poolHour.AreaRuleId)
                        .Where(x => x.SiteId == planningSite.SiteId)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(x => x.Date == historyDate).FirstOrDefaultAsync();

                    var sdkSite = await sdkDbContext.Sites.FirstAsync(x => x.Id == planningSite.SiteId);
                    var language = await sdkDbContext.Languages.FirstAsync(x => x.Id == sdkSite.LanguageId);
                    var mainElement = await sdkCore.ReadeForm(checkList.CheckListId, language);
                    mainElement.Repeated = 0;
                    mainElement.CheckListFolderName = poolDayFolder.MicrotingUid.ToString();
                    mainElement.StartDate = DateTime.Now.ToUniversalTime();
                    mainElement.EndDate = DateTime.Now.AddDays(2).ToUniversalTime();
                    mainElement.Label =
                        $"0. Tidligere indsendt data - {areaRule.AreaRuleTranslations.First().Name}";
                    mainElement.ElementList[0].Label = mainElement.Label;
                    var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZoneInfo);
                    mainElement.ElementList[0].Description = new CDataValue
                    {
                        InderValue =
                            $"Sidst opdateret: {localTime:H:mm}"
                    };
                    ((DataElement) mainElement.ElementList[0]).DataItemList[0].Color =
                        Constants.FieldColors.Yellow;

                    // ((DataElement) mainElement.ElementList[0]).DataItemList[0].Label = $"{poolDayFolder.FolderTranslations.Where(x => x.LanguageId == language.Id).Select(x => x.Name).First()} - {areaRule.AreaRuleTranslations.First().Name}";
                    // ((DataElement) mainElement.ElementList[0]).DataItemList[0].Label =
                    //     regex.Replace(((DataElement) mainElement.ElementList[0]).DataItemList[0].Label,
                    //         "");
                    // ((DataElement) mainElement.ElementList[0]).DataItemList[0].Label = $"{((DataElement) mainElement.ElementList[0]).DataItemList[0].Label}";
                    ((DataElement) mainElement.ElementList[0]).DataItemList[0].Label = " ";
                    var allPoolHourResults = await backendConfigurationPnDbContext.PoolHourResults
                        .Where(x => x.Date >= historyDate)
                        .Where(x => x.Date < historyDate.AddDays(1))
                        .Where(x => x.FolderId == planning.SdkFolderId)
                        .Where(x => x.AreaRuleId == poolHour.AreaRuleId)
                        .OrderBy(x => x.Date)
                        .ToListAsync();
                    StringBuilder header = new StringBuilder();

                    foreach (var hourResult in allPoolHourResults)
                    {

                        localTime = TimeZoneInfo.ConvertTimeFromUtc(hourResult.DoneAt, timeZoneInfo);
                        var selectedPoolHour =
                            await backendConfigurationPnDbContext.PoolHours.FirstAsync(x =>
                                x.Id == hourResult.PoolHourId);
                        header.Append(
                            $"<strong>{selectedPoolHour.Name}:00 {regex.Replace($"{poolDayFolder.FolderTranslations.Where(x => x.LanguageId == language.Id).Select(x => x.Name).First()} - {areaRule.AreaRuleTranslations.First().Name}", "")}</strong>");
                        header.Append($"<br>Dato: {localTime:dd-MM-yyyy}<br>");
                        header.Append($"Tid: {localTime:HH:mm:ss}<br>");
                        header.Append($"Pulsslag ved åbning: {hourResult.PulseRateAtOpening}<br>");
                        header.Append($"Aflæst PH: {hourResult.ReadPhValue}<br>");
                        header.Append($"Aflæst frit klor: {hourResult.ReadFreeChlorine}<br>");
                        header.Append($"Aflæst temp: {hourResult.ReadTemperature}<br>");
                        header.Append($"Antal gæster ved lukning: {hourResult.NumberOfGuestsAtClosing}<br>");
                        header.Append(
                            $"Klarhed: {(hourResult.Clarity == "1" ? "OK" : hourResult.Clarity == null ? "" : "Grumset")}<br>");
                        header.Append($"Målt Frit klor: {hourResult.MeasuredFreeChlorine}<br>");
                        header.Append($"Målt Total klor: {hourResult.MeasuredTotalChlorine}<br>");
                        header.Append($"Målt bundet klor: {hourResult.MeasuredBoundChlorine}<br>");
                        header.Append($"Målt PH: {hourResult.MeasuredPh}<br>");
                        header.Append(
                            $"Kvittering af pulsslag v. åbning: {(hourResult.AcknowledgmentOfPulseRateAtOpening == "1" ? "Ja" : hourResult.AcknowledgmentOfPulseRateAtOpening == null ? "" : "Nej")}<br>");
                        header.Append($"Målt temp. i løbet af dagen: {hourResult.MeasuredTempDuringTheDay}<br>");
                        header.Append($"Kommentar: {hourResult.Comment}<br><br>");
                    }

                    // header = ;
                    ((DataElement) mainElement.ElementList[0]).DataItemList[0].Description = new CDataValue
                    {
                        InderValue =
                            header.ToString()
                    };

                    if (poolHistorySite == null)
                    {
                        var caseId = await sdkCore.CaseCreate(mainElement, "", (int) sdkSite.MicrotingUid,
                            poolDayFolder.Id);
                        poolHistorySite = new PoolHistorySite
                        {
                            AreaRuleId = poolHour.AreaRuleId,
                            Date = historyDate,
                            SiteId = planningSite.SiteId,
                            SdkCaseId = (int) caseId
                        };

                        await poolHistorySite.Create(backendConfigurationPnDbContext);
                    }
                    else
                    {
                        await sdkCore.CaseDelete(poolHistorySite.SdkCaseId);
                        var caseId = await sdkCore.CaseCreate(mainElement, "", (int) sdkSite.MicrotingUid,
                            poolDayFolder.Id);
                        poolHistorySite.SdkCaseId = (int) caseId;
                        await poolHistorySite.Update(backendConfigurationPnDbContext);
                    }

                }
            }
        }
    }
}