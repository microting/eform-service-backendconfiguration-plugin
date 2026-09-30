/*
The MIT License (MIT)

Copyright (c) 2007 - 2021 Microting A/S

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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.EformAngularFrontendBase.Infrastructure.Data;
using Microting.eFormApi.BasePn.Infrastructure.Helpers;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.eFormCaseTemplateBase.Infrastructure.Data;
using Microting.eFormCaseTemplateBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using SendGrid;
using SendGrid.Helpers.Mail;
using Sentry;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Infrastructure.Models;

namespace ServiceBackendConfigurationPlugin.Scheduler.Jobs;

public class SearchListJob : IJob
{
    // private readonly BackendConfigurationDbContextHelper _backendConfigurationDbContextHelper;
    private readonly BackendConfigurationPnDbContext _backendConfigurationDbContext;
    private readonly ChemicalDbContextHelper _chemicalDbContextHelper;
    private readonly eFormCore.Core _core;
    private readonly MicrotingDbContext _sdkDbContext;
    private readonly ItemsPlanningPnDbContext _itemsPlanningPnDbContext;
    private readonly BaseDbContext _baseDbContext;
    private readonly CaseTemplateDbContextHelper _caseTemplateDbContextHelper;

    public SearchListJob(
        BackendConfigurationDbContextHelper dbContextHelper, ChemicalDbContextHelper chemicalDbContextHelper,
        eFormCore.Core core, ItemsPlanningDbContextHelper itemsPlanningDbContextHelper, BaseDbContext baseDbContext,
        CaseTemplateDbContextHelper caseTemplateDbContextHelper)
    {
        _core = core;
        _baseDbContext = baseDbContext;
        _caseTemplateDbContextHelper = caseTemplateDbContextHelper;
        _itemsPlanningPnDbContext = itemsPlanningDbContextHelper.GetDbContext();
        _chemicalDbContextHelper = chemicalDbContextHelper;
        // _backendConfigurationDbContextHelper = dbContextHelper;
        _backendConfigurationDbContext = dbContextHelper.GetDbContext();
        _sdkDbContext = _core.DbContextHelper.GetDbContext();
        _caseTemplateDbContextHelper = caseTemplateDbContextHelper;
        // _itemsPlanningDbContextHelper = itemsPlanningDbContextHelper.GetDbContext();
    }

    public async Task Execute()
    {
        await ExecuteUpdateProperties();
    }

    private async Task ExecuteUpdateProperties()
    {
        var customerNo = _sdkDbContext.Settings.First(x => x.Name == "customerNo").Value;

        switch (DateTime.UtcNow.Hour)
        {
            case 2:
                try
                {
                    Log.LogEvent(
                        "SearchListJob.Task: SearchListJob.Execute got called at 2am - chemicalbase updates");
                    const string url = "https://chemicalbase.microting.com/get-all-chemicals";
                    using var client = new HttpClient();
                    var outcome = await ChemicalFeedGuard.RunAsync(
                        () => client.GetAsync(url),
                        CountActiveLocalChemicals,
                        UpsertChemical,
                        RemoveChemicalsMissingFromFeed,
                        new ParallelOptions { MaxDegreeOfParallelism = -1 }).ConfigureAwait(false);
                    ReportChemicalSync(outcome);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"fail: {e.Message}");
                    Console.WriteLine($"fail: {e.StackTrace}");
                    SentrySdk.CaptureException(e);
                }

                break;
            case 18:
            {
                // ShowExpireDate is a *display* flag on the items-planning Planning - see
                // ServiceItemsPlanningPlugin.Handlers.ItemCaseCreateHandler, which reads it to decide
                // whether an expiry is stamped on the deployed case. What `ShowExpireDate == false` was
                // ever meant to signal *here* is recorded nowhere in the codebase, and whether this job
                // should delete anything at all is an open product question (issue #1262). Until that is
                // answered the job keeps its existing shape, with two guards added: plannings owned by a
                // live AreaRulePlanning are never touched, and a failure no longer deletes.
                try
                {
                    // AsNoTracking is load bearing. Planning.Delete (PnBase) mutates the entity and
                    // calls SaveChangesAsync on this shared context directly, so a tracked candidate
                    // whose save was rejected would stay tracked as Modified and be bundled into - and
                    // break - the save of every later planning in the loop. Candidates are read
                    // detached; the loop below re-fetches the one planning it is about to delete.
                    var candidatePlannings = await _itemsPlanningPnDbContext.Plannings
                        .AsNoTracking()
                        .Where(x => x.ShowExpireDate == false)
                        .Where(x => x.Enabled)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed).ToListAsync();

                    // The items-planning and backend-configuration contexts address separate databases,
                    // so ownership cannot be expressed as a LINQ join. PlanningOwnershipHelper resolves
                    // it with a second, batched query against AreaRulePlannings instead.
                    var brokenPlannings = await PlanningOwnershipHelper
                        .ExcludeBackendConfigurationOwnedAsync(candidatePlannings, _backendConfigurationDbContext)
                        .ConfigureAwait(false);

                    var backendConfigurationOwnedCount = candidatePlannings.Count - brokenPlannings.Count;
                    if (backendConfigurationOwnedCount > 0)
                    {
                        Log.LogEvent(
                            $"info: SearchListJob.Task: Skipped {backendConfigurationOwnedCount} planning(s) with ShowExpireDate set to false, because they are owned by a live AreaRulePlanning.");
                    }

                    // Log.LogEvent("SearchListJob.Task: SearchListJob.Execute got called at 5:00 - Documents");
                    var property = await _backendConfigurationDbContext.Properties
                        .Where(x => x.MainMailAddress != null && x.MainMailAddress != "")
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed).FirstOrDefaultAsync();

                    if (property == null)
                    {
                        return;
                    }
                    //
                    // var caseTemplateDbContext = _caseTemplateDbContextHelper.GetDbContext();
                    var sendGridKey =
                        _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                    //

                        //
                    var fromEmailAddress = new EmailAddress("no-reply@microting.com",
                        $"Planning ShowExpireDate set to false: {customerNo}");
                    var toEmailAddress = new List<EmailAddress>();
                    // if (!string.IsNullOrEmpty(property.MainMailAddress))
                    // {
                    //     toEmailAddress.AddRange(
                    //         property.MainMailAddress.Split(";").Select(s => new EmailAddress(s)));
                    // }
                    toEmailAddress.Add(new EmailAddress("rm@microting.com"));

                    if (toEmailAddress.Count > 0 && !string.IsNullOrEmpty(sendGridKey.Value) && brokenPlannings.Count > 0)
                    {
                        var sendGridClient = new SendGridClient(sendGridKey.Value);

                        var stringBuilder = new StringBuilder();
                        stringBuilder.Append("<html><body>");

                        var brokenPlanningIds = brokenPlannings.Select(x => x.Id).Distinct().ToList();

                        // Every name in one batched pass instead of a query per planning inside the
                        // loop, which keeps the round trip count at roughly what it was before the
                        // per-planning re-fetch was introduced. First translation per planning wins,
                        // which is what the previous unordered FirstAsync resolved to in practice.
                        var planningNames = new Dictionary<int, string>();
                        foreach (var idBatch in PlanningOwnershipHelper.BatchPlanningIds(brokenPlanningIds))
                        {
                            var translations = await _itemsPlanningPnDbContext.PlanningNameTranslation
                                .AsNoTracking()
                                .Where(x => idBatch.Contains(x.PlanningId))
                                .OrderBy(x => x.Id)
                                .Select(x => new { x.PlanningId, x.Name })
                                .ToListAsync();
                            foreach (var translation in translations)
                            {
                                planningNames.TryAdd(translation.PlanningId, translation.Name);
                            }
                        }

                        var sweepResults = await PlanningOwnershipHelper.ProcessPlanningsIndividuallyAsync(
                            brokenPlanningIds,
                            async planningId =>
                            {
                                if (!planningNames.TryGetValue(planningId, out var planningName))
                                {
                                    // Same guard the previous per-planning FirstAsync gave us: a
                                    // planning we cannot even name is not one we should destroy.
                                    throw new InvalidOperationException(
                                        $"No PlanningNameTranslation row found for planning with id: {planningId}.");
                                }

                                // Fetched fresh, one at a time, so the only planning tracked by the
                                // shared context is the one being deleted right now.
                                var planning = await _itemsPlanningPnDbContext.Plannings
                                    .FirstOrDefaultAsync(x => x.Id == planningId);
                                if (planning == null)
                                {
                                    throw new InvalidOperationException(
                                        $"Planning with id: {planningId} could not be loaded for deletion.");
                                }

                                await planning.Delete(_itemsPlanningPnDbContext).ConfigureAwait(false);
                                return planningName;
                            },
                            () => _itemsPlanningPnDbContext.ChangeTracker.Clear())
                            .ConfigureAwait(false);

                        foreach (var sweepResult in sweepResults)
                        {
                            if (sweepResult.Succeeded)
                            {
                                stringBuilder.Append(
                                    $"<p>Planning with id: {sweepResult.PlanningId} and name: {sweepResult.PlanningName} has ShowExpireDate set to false</p>");
                                continue;
                            }

                            // Deliberately no delete here. A planning we could not process is not a
                            // planning we should destroy.
                            Log.LogException(
                                $"SearchListJob.Task: case 18 - planning with id: {sweepResult.PlanningId} could not be processed and was NOT deleted. {sweepResult.FailureException.Message}");
                            SentrySdk.CaptureException(sweepResult.FailureException);
                            stringBuilder.Append(
                                $"<p>Planning with id: {sweepResult.PlanningId} has ShowExpireDate set to false and could not be processed (not deleted): {sweepResult.FailureException.Message}</p>");
                        }

                        stringBuilder.Append("</body></html>");

                        var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress,
                            toEmailAddress,
                            $"Planning ShowExpireDate set to false: {customerNo}", null, stringBuilder.ToString());

                        var responseMessage = await sendGridClient.SendEmailAsync(msg);
                        if ((int) responseMessage.StatusCode < 200 ||
                            (int) responseMessage.StatusCode >= 300)
                        {
                            throw new Exception($"Status: {responseMessage.StatusCode}");
                        }
                    }
                }
                catch (Exception caseException)
                {
                    // The timer callback in Core.cs is `async void`, so anything escaping this case
                    // would surface as an unhandled exception on the thread pool.
                    Log.LogException(
                        $"SearchListJob.Task: case 18 (ShowExpireDate sweep) failed: {caseException.Message}");
                    SentrySdk.CaptureException(caseException);
                }
            }
                break;
            case 9:
            {
                // #1325 — runs before the movement flag check below: retracting the
                // device case of missed hidden-overdue occurrences is not gated by it.
                try
                {
                    Log.LogEvent("info: SearchListJob.Task: SearchListJob.Execute got called in the 9 UTC hour - retract hidden overdue cases");
                    await new HiddenOverdueCaseRetractor(_backendConfigurationDbContext, _sdkDbContext, _core)
                        .RetractAsync(DateTime.UtcNow);
                }
                catch (Exception retractException)
                {
                    Log.LogException(
                        $"SearchListJob.Task: case 9 (retract hidden overdue cases) failed: {retractException.Message}");
                    SentrySdk.CaptureException(retractException);
                }

                /* Find all compliances which have expired today and we have call sdk and move the eform from the current folder to the expired folder
                 * also we need to set the ignore_end_date, when doing the call.
                 * The expired folder is found by looking at the area rule -> area -> expired folder
                 */
                var complianceMovementIsEnabled = await _backendConfigurationDbContext.PluginConfigurationValues
                    .FirstOrDefaultAsync(x => x.Name == "BackendConfigurationSettings:ComplianceOverdueMovementEnabled");

                if (complianceMovementIsEnabled == null || !bool.TryParse(complianceMovementIsEnabled.Value, out var isEnabled) || !isEnabled)
                {
                    Log.LogEvent("info: SearchListJob.Task: Compliance overdue movement is disabled. Exiting.");
                    break;
                }

                var changeDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, 0, 0, 0).AddDays(-1);

                var complianceList = await _backendConfigurationDbContext.Compliances
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.Deadline < DateTime.UtcNow)
                    .Where(x => x.Deadline > changeDate)
                    .Where(x => x.MovedToExpiredFolder == false)
                    .AsNoTracking()
                    .OrderBy(x => x.Deadline)
                    .ToListAsync();

                var listOfPropertieIdsFromComplianceList= complianceList
                    .Select(x => x.PropertyId)
                    .Distinct()
                    .ToList();

                var properties = await _backendConfigurationDbContext.Properties
                    .Where(x => listOfPropertieIdsFromComplianceList.Contains(x.Id))
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .ToListAsync();

                foreach (var compliance in complianceList)
                {
                    var areaRulePlanningQuery = _backendConfigurationDbContext.AreaRulePlannings
                        .Include(x => x.AreaRulePlanningTags)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(x => x.ItemPlanningId == compliance.PlanningId);

                    var areaRulePlanning = await areaRulePlanningQuery
                        .Select(x => new { x.AreaRuleId, x.StartDate, x.Id, x.ComplianceEnabled })
                        .FirstOrDefaultAsync();

                    if (!areaRulePlanning.ComplianceEnabled)
                    {
                        continue;
                    }

                    var microtingSdkCaseId = compliance.MicrotingSdkCaseId;
                    var theCase = await _sdkDbContext.Cases.Where(x => x.Id == microtingSdkCaseId)
                        // .Select(x => x.MicrotingUid)
                        .FirstAsync();

                    var property = properties
                        // .Select(x => x.FolderId)
                        .First(x => x.Id == compliance.PropertyId);

                    var folderAndFolderTranslation = await _sdkDbContext.Folders
                        .Join(_sdkDbContext.FolderTranslations,
                            folder => folder.Id,
                            folderTranslation => folderTranslation.FolderId,
                            (folder, folderTranslation) => new {folder, folderTranslation})
                        .Where(x => x.folder.ParentId == property.FolderId &&
                                    x.folderTranslation.Name == "00. Overdue tasks")
                        .FirstOrDefaultAsync();

                    var site = await _sdkDbContext.Sites
                        .Where(x => x.Id == theCase.SiteId)
                        .Select(x => x.MicrotingUid)
                        .FirstAsync();

                    await _core.UpdateDeployedeForm((int)theCase.MicrotingUid, site.ToString(), (int)folderAndFolderTranslation.folder.MicrotingUid, true);
                    Console.WriteLine($"info: Moved compliance case with id: {theCase.Id} to overdue folder");
                    var comp = await _backendConfigurationDbContext.Compliances.FirstAsync(x => x.Id == compliance.Id);
                    comp.MovedToExpiredFolder = true;
                    await comp.Update(_backendConfigurationDbContext);
                }
                break;
            }
            case 8:
            {

                // if today is not monday, then continue
                if (DateTime.Now.DayOfWeek != DayOfWeek.Monday)
                {
                    return;
                }

                Log.LogEvent("info: SearchListJob.Task: SearchListJob.Execute got called at 8:00 UTC - Opgavestatus");
                var properties = await _backendConfigurationDbContext.Properties
                    .Where(x => x.MainMailAddress != null && x.MainMailAddress != "")
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed).ToListAsync();

                var sendGridKey =
                    _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                var danishLanguage = await _sdkDbContext.Languages.FirstAsync(x => x.LanguageCode == "da")
                    .ConfigureAwait(false);

                foreach (var property in properties)
                {
                    var fromEmailAddress = new EmailAddress("no-reply@microting.com",
                        $"Opgavestatus: {customerNo} {property.Name}");
                    var toEmailAddress = new List<EmailAddress>();
                    if (!string.IsNullOrEmpty(property.MainMailAddress))
                    {
                        toEmailAddress.AddRange(
                            property.MainMailAddress.Split(";").Select(s => new EmailAddress(s.Trim())));
                    }

                    if (toEmailAddress.Count > 0 && !string.IsNullOrEmpty(sendGridKey.Value))
                    {
                        try
                        {
                            var today = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0);
                            var complianceList = await _backendConfigurationDbContext.Compliances
                                .Where(x => x.PropertyId == property.Id)
                                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                .AsNoTracking()
                                .OrderBy(x => x.Deadline)
                                .ToListAsync();

                            var startOfLast24Hours = today.AddDays(-1);

                            var completedComplianceWithinLast24HoursList = await _backendConfigurationDbContext
                                .Compliances
                                .Where(x => x.PropertyId == property.Id)
                                .Where(x => x.WorkflowState == Constants.WorkflowStates.Removed)
                                .Where(x => x.MicrotingSdkCaseId != 0)
                                .Where(x => x.UpdatedAt > startOfLast24Hours)
                                .AsNoTracking()
                                .OrderBy(x => x.Deadline)
                                .ToListAsync();

                            foreach (var compliance in completedComplianceWithinLast24HoursList)
                            {
                                var sdkCase =
                                    await _sdkDbContext.Cases.FirstAsync(x => x.Id == compliance.MicrotingSdkCaseId);
                                if (sdkCase.Status == 100)
                                {
                                    complianceList.Add(compliance);
                                }
                            }

                            var entities = new List<ComplianceModel>();

                            Log.LogEvent("info: Opgavestatus. Found " + complianceList.Count + " compliances for property: " +
                                         property.Name);
                            foreach (var compliance in complianceList)
                            {
                                var language = await _sdkDbContext.Languages.FirstAsync(x => x.LanguageCode == "da")
                                    .ConfigureAwait(false);
                                var planningNameTranslation = await _itemsPlanningPnDbContext.PlanningNameTranslation
                                    .SingleOrDefaultAsync(x =>
                                        x.PlanningId == compliance.PlanningId && x.LanguageId == language.Id)
                                    .ConfigureAwait(false);

                                if (planningNameTranslation == null)
                                {
                                    continue;
                                }

                                var areaTranslation = await _backendConfigurationDbContext.AreaTranslations
                                    .SingleOrDefaultAsync(x =>
                                        x.AreaId == compliance.AreaId && x.LanguageId == language.Id)
                                    .ConfigureAwait(false);

                                if (areaTranslation == null)
                                {
                                    continue;
                                }

                                var planningSites = await _itemsPlanningPnDbContext.PlanningSites
                                    .Where(x => x.PlanningId == compliance.PlanningId)
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .Select(x => x.SiteId)
                                    .Distinct()
                                    .ToListAsync().ConfigureAwait(false);

                                var sdkFolderId = await _itemsPlanningPnDbContext.Plannings
                                    .Where(x => x.Id == compliance.PlanningId)
                                    .Select(x => x.SdkFolderId)
                                    .FirstOrDefaultAsync()
                                    .ConfigureAwait(false);

                                if (sdkFolderId is 0 or null)
                                {
                                    // send email to RM about missing folder
                                    MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress,
                                        [new EmailAddress("rm@microting.dk")],
                                        $"Missing folder for compliance: {customerNo} {property.Name}",
                                        $"Compliance with id: {compliance.Id} is missing a folder",
                                        $"Compliance with id: {compliance.Id} is missing a folder");
                                }

                                var sdkFolderName = await _sdkDbContext.FolderTranslations
                                    .Where(x => x.FolderId == sdkFolderId)
                                    .Where(x => x.LanguageId == danishLanguage.Id)
                                    .Select(x => x.Name)
                                    .FirstOrDefaultAsync() ?? await _itemsPlanningPnDbContext.Plannings
                                    .Where(x => x.Id == compliance.PlanningId)
                                    .Select(x => x.SdkFolderName)
                                    .FirstAsync()
                                    .ConfigureAwait(false);

                                var sitesList = await _sdkDbContext.Sites.Where(x => planningSites.Contains(x.Id))
                                    .ToListAsync()
                                    .ConfigureAwait(false);

                                var responsible = sitesList
                                    .Select(site => new KeyValuePair<int, string>(site.Id, site.Name))
                                    .ToList();

                                var complianceModel = new ComplianceModel
                                {
                                    CaseId = compliance.MicrotingSdkCaseId,
                                    CreatedAt = compliance.CreatedAt,
                                    Deadline = compliance.Deadline.AddDays(-1),
                                    ComplianceTypeId = null,
                                    ControlArea = areaTranslation.Name,
                                    EformId = compliance.MicrotingSdkeFormId,
                                    Id = compliance.Id,
                                    ItemName = planningNameTranslation.Name,
                                    PlanningId = compliance.PlanningId,
                                    Responsible = responsible,
                                    FolderName = sdkFolderName,
                                    WorkflowState = compliance.WorkflowState
                                };

                                entities.Add(complianceModel);
                            }

                            var expiredTodayModels = new List<ComplianceModel>();
                            var expiredComplianceModels = new List<ComplianceModel>();
                            var expiredLast24HoursModels = new List<ComplianceModel>();
                            var completedLast24HoursModels = new List<ComplianceModel>();
                            var expiringIn1Month = new List<ComplianceModel>();
                            // var expiringIn3Months = new List<ComplianceModel>();
                            // var expiringIn6Months = new List<ComplianceModel>();
                            // var expiringIn12Months = new List<ComplianceModel>();
                            var expiringOver1Month = new List<ComplianceModel>();
                            var hasCompliances = false;

                            var removed = entities.Where(x => x.WorkflowState == Constants.WorkflowStates.Removed);
                            foreach (var complianceModel in removed)
                            {
                                var sdkCase =
                                    await _sdkDbContext.Cases.FirstAsync(x => x.Id == complianceModel.CaseId);
                                complianceModel.Deadline = sdkCase.DoneAtUserModifiable!.Value;
                                completedLast24HoursModels.Add(complianceModel);
                                hasCompliances = true;
                            }

                            var notRemoved = entities.Where(x => x.WorkflowState != Constants.WorkflowStates.Removed);

                            var tomorrow =
                                new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0)
                                    .AddDays(1);
                            foreach (var complianceModel in notRemoved)
                            {
                                if (complianceModel.Deadline < DateTime.Now.AddDays(-1) &&
                                    complianceModel.Deadline > DateTime.Now.AddDays(-2))
                                {
                                    expiredLast24HoursModels.Add(complianceModel);
                                    expiredComplianceModels.Add(complianceModel);
                                    hasCompliances = true;
                                }
                                else if (complianceModel.Deadline >= today &&
                                         complianceModel.Deadline < tomorrow)
                                {
                                    expiredTodayModels.Add(complianceModel);
                                    hasCompliances = true;
                                }
                                else if (complianceModel.Deadline < DateTime.Now)
                                {
                                    expiredComplianceModels.Add(complianceModel);
                                    hasCompliances = true;
                                }
                                else if (complianceModel.Deadline < DateTime.Now.AddMonths(1))
                                {
                                    expiringIn1Month.Add(complianceModel);
                                    hasCompliances = true;
                                }
                                else
                                {
                                    expiringOver1Month.Add(complianceModel);
                                    hasCompliances = true;
                                }
                            }

                            var sendGridClient = new SendGridClient(sendGridKey.Value);
                            var assembly = Assembly.GetExecutingAssembly();
                            var assemblyName = assembly.GetName().Name;

                            var stream =
                                assembly.GetManifestResourceStream(
                                    $"{assemblyName}.Resources.new_compliance_report.html");
                            string html;
                            if (stream == null)
                            {
                                throw new InvalidOperationException("Resource not found");
                            }

                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                html = await reader.ReadToEndAsync();
                            }


                            var newHtml = html;
                            newHtml = newHtml.Replace("{{propertyName}}", property.Name);
                            TimeZoneInfo copenhagenTimeZone =
                                TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
                            DateTime utcTime = DateTime.UtcNow;
                            DateTime copenhagenTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, copenhagenTimeZone);
                            newHtml = newHtml.Replace("{{dato}}", copenhagenTime.ToString("dd-MM-yyyy HH:mm:ss"));
                            newHtml = newHtml.Replace("{{emailaddresses}}", property.MainMailAddress);

                            // if (DateTime.Now.DayOfWeek == DayOfWeek.Thursday && hasCompliances)
                            // {
                            newHtml = newHtml.Replace("{{expiredTodayProducts}}",
                                await GenerateComplianceList(expiredTodayModels, property.Name));
                            newHtml = newHtml.Replace("{{expiredProducts}}",
                                await GenerateComplianceList(expiredComplianceModels, property.Name));
                            newHtml = newHtml.Replace("{{expiringIn1Month}}",
                                await GenerateComplianceList(expiringIn1Month, property.Name));
                            newHtml = newHtml.Replace("{{expiredLast24Hours}}",
                                await GenerateComplianceList(expiredLast24HoursModels, property.Name));
                            newHtml = newHtml.Replace("{{doneLast24Hours}}",
                                await GenerateComplianceList(completedLast24HoursModels, property.Name));
                            // newHtml = newHtml.Replace("{{expiringIn3Months}}",
                            //     await GenerateComplianceList(expiringIn3Months, property.Name));
                            // newHtml = newHtml.Replace("{{expiringIn6Months}}",
                            //     await GenerateComplianceList(expiringIn6Months, property.Name));
                            // newHtml = newHtml.Replace("{{expiringIn12Months}}",
                            //     await GenerateComplianceList(expiringIn12Months, property.Name));
                            newHtml = newHtml.Replace("{{expiringOver1Month}}",
                                await GenerateComplianceList(expiringOver1Month, property.Name));

                            List<Attachment> attachments = new List<Attachment>();

                            newHtml = newHtml.Replace("{{customerNo}}", customerNo);
                            newHtml = newHtml.Replace("{{numberOfExpiredTasks}}",
                                expiredComplianceModels.Count.ToString());

                            var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress,
                                toEmailAddress,
                                $"Opgavestatus: {customerNo} {property.Name}", null, newHtml);
                            // msg.AddAttachments(attachments);

                            var responseMessage = await sendGridClient.SendEmailAsync(msg);
                            if ((int)responseMessage.StatusCode < 200 ||
                                (int)responseMessage.StatusCode >= 300)
                            {
                                throw new Exception($"Status: {responseMessage.StatusCode}");
                            }
                            //}
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"fail: {e.Message}");
                            Console.WriteLine($"fail: {e.StackTrace}");
                            SentrySdk.CaptureException(e);
                        }
                    }
                }
            }
                break;
        }


        try
        {
            var emails = _backendConfigurationDbContext.Emails
                .Where(x => x.Sent == null)
                .Where(x => x.DelayedUntil < DateTime.UtcNow)
                .ToList();

            foreach (var email in emails)
            {
                var sendGridKey =
                    _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");
                var client = new SendGridClient(sendGridKey.Value);
                var fromEmailAddress = new EmailAddress("no-reply@microting.com");
                var toEmailAddresses = new List<EmailAddress>();
                if (!string.IsNullOrEmpty(email.To))
                {
                    toEmailAddresses.AddRange(email.To.Split(";").Select(s => new EmailAddress(s)));
                }

                var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress, toEmailAddresses,
                    email.Subject, "", email.Body);

                var emailAttachments = await _backendConfigurationDbContext.EmailAttachments
                    .Where(x => x.EmailId == email.Id).ToListAsync();

                List<Attachment> attachments = new List<Attachment>();
                var assembly = Assembly.GetExecutingAssembly();
                var assemblyName = assembly.GetName().Name;
                foreach (var emailAttachment in emailAttachments)
                {
                    var stream =
                        assembly.GetManifestResourceStream(
                            $"{assemblyName}.Resources.{emailAttachment.ResourceName}");
                    if (stream == null)
                    {
                        throw new InvalidOperationException("Resource not found");
                    }

                    byte[] bytes;
                    using (var memoryStream = new MemoryStream())
                    {
                        await stream.CopyToAsync(memoryStream);
                        bytes = memoryStream.ToArray();
                    }

                    var attachment1 = new Attachment
                    {
                        Filename = emailAttachment.ResourceName,
                        Content = Convert.ToBase64String(bytes),
                        ContentId = emailAttachment.CidName,
                        Disposition = "inline"
                    };
                    attachments.Add(attachment1);
                }

                msg.AddAttachments(attachments);

                var response = await client.SendEmailAsync(msg);
                if ((int) response.StatusCode < 200 || (int) response.StatusCode >= 300)
                {
                    email.Error = $"Status: {response.StatusCode}";
                    await email.Update(_backendConfigurationDbContext).ConfigureAwait(false);
                }
                else
                {
                    email.SentAt = DateTime.UtcNow;
                    email.Sent = response.StatusCode.ToString();
                    email.Status = "Sent";
                    await email.Update(_backendConfigurationDbContext).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: {e.Message}");
            Console.WriteLine($"fail: {e.StackTrace}");
            SentrySdk.CaptureException(e);
        }
    }


    private async Task<int> CountActiveLocalChemicals()
    {
        await using var chemicalsDbContext = _chemicalDbContextHelper.GetDbContext();
        return await chemicalsDbContext.Chemicals
            .CountAsync(x => x.WorkflowState != Constants.WorkflowStates.Removed).ConfigureAwait(false);
    }

    /// <summary>Upserts one feed chemical, matched on RemoteId (see ChemicalFeedGuard).</summary>
    private async ValueTask UpsertChemical(Chemical chemical, CancellationToken ct)
    {
        if (chemical.WorkflowState == Constants.WorkflowStates.Removed)
        {
            Console.WriteLine($"info: Chemical is removed so skipping : {chemical.Name}");
            return;
        }

        await using var chemicalsDbContext = _chemicalDbContextHelper.GetDbContext();
        var localRows = await chemicalsDbContext.Chemicals
            .Include(x => x.Products)
            .Where(x => x.RemoteId == chemical.RemoteId)
            .ToListAsync(ct).ConfigureAwait(false);
        var c = ChemicalFeedGuard.PickLocalMatch(localRows);
        if (c == null)
        {
            Console.WriteLine($"info: Chemical does not exist, so creating : {chemical.Name}");
            await chemical.Create(chemicalsDbContext).ConfigureAwait(false);
            return;
        }

        if (ChemicalFeedGuard.TryRestore(c))
        {
            Console.WriteLine($"info: Chemical was removed locally but is in the feed again, restoring : {chemical.Name}");
        }

        // Keys and names follow the feed, so a changed RegistrationNo never leaves a stale
        // local value behind. Blank feed values never overwrite local ones.
        if (!string.IsNullOrEmpty(chemical.RegistrationNo))
        {
            c.RegistrationNo = chemical.RegistrationNo;
        }

        if (!string.IsNullOrEmpty(chemical.Name))
        {
            c.Name = chemical.Name;
        }

        c.Use = chemical.Use;
        c.Verified = chemical.Verified;
        c.AuthorisationDate = chemical.AuthorisationDate;
        c.AuthorisationExpirationDate = chemical.AuthorisationExpirationDate;
        c.AuthorisationTerminationDate = chemical.AuthorisationTerminationDate;
        c.UseAndPossesionDeadline = chemical.UseAndPossesionDeadline;
        c.PossessionDeadline = chemical.PossessionDeadline;
        c.SalesDeadline = chemical.SalesDeadline;
        c.Status = chemical.Status;
        c.PesticideUser = chemical.PesticideUser;
        c.FormulationType = chemical.FormulationType;
        c.FormulationSubType = chemical.FormulationSubType;
        c.BiocideAuthorisationType = chemical.BiocideAuthorisationType;
        c.PesticidePossibleUse = chemical.PesticidePossibleUse;
        c.PesticideProductGroup = chemical.PesticideProductGroup;
        c.BiocidePossibleUse = chemical.BiocidePossibleUse;
        c.BiocideSpecialUse = chemical.BiocideSpecialUse;
        c.BiocideProductType = chemical.BiocideProductType;
        c.BiocideUser = chemical.BiocideUser;
        c.PestControlType = chemical.PestControlType;
        c.BarcodeValue = chemical.BarcodeValue;
        c.BiocideProductGroup = chemical.BiocideProductGroup;
        if (!chemicalsDbContext.AuthorisationHolders.Any(x =>
                x.RemoteId == chemical.AuthorisationHolder.RemoteId))
        {
            var ah = new AuthorisationHolder
            {
                RemoteId = chemical.AuthorisationHolder.RemoteId,
                Name = chemical.AuthorisationHolder.Name,
                Address = chemical.AuthorisationHolder.Address
            };
            await ah.Create(chemicalsDbContext).ConfigureAwait(false);
            c.AuthorisationHolderId = ah.Id;
        }
        else
        {
            c.AuthorisationHolderId = chemicalsDbContext.AuthorisationHolders.First(x =>
                x.RemoteId == chemical.AuthorisationHolder.RemoteId).Id;
        }

        if (chemical.Products.Count != c.Products.Count)
        {
            foreach (var chemicalProduct in chemical.Products)
            {
                var dbProduct = await chemicalsDbContext.Products.FirstOrDefaultAsync(
                    x =>
                        x.ChemicalId == c.Id && x.FileName == chemicalProduct.FileName);
                if (dbProduct == null)
                {
                    dbProduct = new Product
                    {
                        FileName = chemicalProduct.FileName,
                        Barcode = chemicalProduct.Barcode,
                        ChemicalId = c.Id,
                        Checksum = ""
                    };
                    await dbProduct.Create(chemicalsDbContext);
                }
                else
                {
                    dbProduct.Barcode = chemicalProduct.Barcode;
                    dbProduct.Name = chemicalProduct.Name;
                    dbProduct.Checksum = chemicalProduct.Checksum;
                    await dbProduct.Update(chemicalsDbContext);
                }
            }
        }
        else
        {
            foreach (var cProduct in c.Products)
            {
                var dbProduct =
                    await chemicalsDbContext.Products.FirstAsync(x =>
                        x.Id == cProduct.Id);
                foreach (var chemicalProduct in chemical.Products)
                {
                    if (chemicalProduct.Name == cProduct.Name)
                    {
                        dbProduct.FileName = chemicalProduct.FileName;
                        dbProduct.Barcode = chemicalProduct.Barcode;
                        await dbProduct.Update(chemicalsDbContext);
                    }
                }
            }
        }

        await c.Update(chemicalsDbContext).ConfigureAwait(false);
    }

    /// <summary>
    /// Soft-deletes active local chemicals whose RemoteId is not in the feed. Only reached when
    /// ChemicalFeedGuard accepted the feed; local rows without a RemoteId are never removed.
    /// </summary>
    private async Task RemoveChemicalsMissingFromFeed(IReadOnlySet<string> feedRemoteIds)
    {
        await using var chemicalsDbContext = _chemicalDbContextHelper.GetDbContext();
        var activeLocals = await chemicalsDbContext.Chemicals
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .ToListAsync().ConfigureAwait(false);

        var unkeyed = activeLocals.Count(x => string.IsNullOrWhiteSpace(x.RemoteId));
        if (unkeyed > 0)
        {
            Log.LogEvent(
                $"SearchListJob.Task: chemicalbase updates - {unkeyed} local chemical(s) without RemoteId are kept (never removed by the sync)");
        }

        foreach (var chemical in ChemicalFeedGuard.SelectToRemove(feedRemoteIds, activeLocals))
        {
            Console.WriteLine($@"info: Deleting chemical: {chemical.Name}");
            await chemical.Delete(chemicalsDbContext).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Logs every run; reports skipped runs to Sentry under one constant message and a per-skip
    /// fingerprint, so all tenants group into one issue. An outage (HTTP failure, no or empty feed)
    /// is a Warning; a partial feed is an Error.
    /// </summary>
    private static void ReportChemicalSync(ChemicalSyncOutcome outcome)
    {
        var skip = outcome.Decision.Skip;
        var counts =
            $"HTTP {(int)outcome.StatusCode}, feed rows {outcome.FeedRows?.ToString() ?? "n/a"}, feed RemoteIds {outcome.FeedRemoteIds}, local active {outcome.LocalActiveCount?.ToString() ?? "n/a"}";
        if (skip == ChemicalSyncSkip.None)
        {
            Log.LogEvent($"SearchListJob.Task: chemicalbase updates applied ({counts})");
            return;
        }

        var skipped = outcome.Decision.ApplyUpserts ? "removals" : "all changes";
        Log.LogEvent($"SearchListJob.Task: chemicalbase updates - {skip}, {skipped} skipped ({counts})");
        SentrySdk.CaptureMessage("SearchListJob chemicalbase sync: register changes skipped", scope =>
            {
                scope.SetFingerprint(new[] { "chemicalbase-sync-skipped", skip.ToString() });
                scope.SetTag("chemical_sync_skip", skip.ToString());
                scope.SetExtra("httpStatus", (int)outcome.StatusCode);
                scope.SetExtra("feedRows", outcome.FeedRows);
                scope.SetExtra("feedRemoteIds", outcome.FeedRemoteIds);
                scope.SetExtra("localActiveCount", outcome.LocalActiveCount);
            },
            skip == ChemicalSyncSkip.PartialFeed ? SentryLevel.Error : SentryLevel.Warning);
    }

    private async Task<string> GenerateDocumentList(List<DocumentProperty> documentProperties,
        CaseTemplatePnDbContext caseTemplatePnDbContext,
        BackendConfigurationPnDbContext backendConfigurationPnDbContext)
    {
        string result = "";

        foreach (var documentProperty in documentProperties.OrderBy(x => x.ExpireDate))
        {
            var document = await caseTemplatePnDbContext.Documents
                .Include(x => x.DocumentTranslations)
                .Include(x => x.DocumentProperties)
                .FirstOrDefaultAsync(x => x.Id == documentProperty.DocumentId);

            if (document == null)
            {
                continue;
            }

            var folderName = await caseTemplatePnDbContext.FolderTranslations
                .Where(y => y.LanguageId == 1)
                .Where(x => x.FolderId == document.FolderId).Select(x => x.Name).FirstAsync();

            var properties = await backendConfigurationPnDbContext.Properties
                .Where(x => document.DocumentProperties.Select(y => y.PropertyId).Contains(x.Id))
                .Select(x => x.Name).ToListAsync();

            result += "<tr valign=\"top\">" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{document.Id}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{string.Join("<br>", properties)}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{folderName}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{document.DocumentTranslations.First(x => x.LanguageId == 1).Name}</span></p>" +
                      "</td><td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{document.DocumentTranslations.First(x => x.LanguageId == 1).Description}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{document.EndAt:dd-MM-yyyy}</span></p>" +
                      "</td>" +
                      "</tr>";
        }

        return result;
    }

    private async Task<string> GenerateComplianceList(List<ComplianceModel> complianceModels, string propertyName)
    {
        string result = "";

        foreach (var complianceModel in complianceModels.OrderBy(x => x.Deadline))
        {

            var responsible = "";
            foreach (var keyValuePair in complianceModel.Responsible)
            {
                responsible += keyValuePair.Value + "<br>";
            }

            result += "<tr valign=\"top\">" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{complianceModel.Id}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{propertyName}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{complianceModel.FolderName}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{complianceModel.ItemName}</span></p>" +
                      "</td><td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{responsible}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{complianceModel.Deadline:dd-MM-yyyy}</span></p>" +
                      "</td>" +
                      "</tr>";
        }

        return result;
    }

}