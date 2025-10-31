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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Models;
using Microting.EformAngularFrontendBase.Infrastructure.Data;
using Microting.eFormApi.BasePn.Infrastructure.Helpers;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.eFormCaseTemplateBase.Infrastructure.Data;
using Microting.eFormCaseTemplateBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using SendGrid;
using SendGrid.Helpers.Mail;
using Sentry;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Infrastructure.Models;
using ServiceBackendConfigurationPlugin.Infrastructure.Models.AreaRules;
using File = System.IO.File;
using PlanningSite = Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.PlanningSite;

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
                    var url = "https://chemicalbase.microting.com/get-all-chemicals";
                    var client = new HttpClient();
                    var response = await client.GetAsync(url).ConfigureAwait(false);
                    var result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    JsonSerializerOptions options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
                    };
                    List<Chemical> chemicals = JsonSerializer.Deserialize<List<Chemical>>(result, options);

                    List<string> regNos = new List<string>();
                    if (chemicals != null)
                    {
                        int count = chemicals.Count;
                        int i = 0;
                        var parallelOptions = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = -1
                        };
                        // foreach (var chemical in chemicals)
                        await Parallel.ForEachAsync(chemicals, parallelOptions, async (chemical, ct) =>
                        {
                            var chemicalsDbContext = _chemicalDbContextHelper.GetDbContext();
                            var c = await chemicalsDbContext.Chemicals
                                .Include(x => x.Products)
                                .FirstOrDefaultAsync(x => x.RemoteId == chemical.RemoteId);
                            regNos.Add(chemical.RegistrationNo);
                            if (c != null)
                            {
                                if (chemical.WorkflowState != Constants.WorkflowStates.Removed)
                                {
                                    // Console.WriteLine(
                                        // $"Chemical already exist, so updating : {chemical.Name} no {i} of {count}");
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
                                    // chemical.Id = c.Id;
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
                                else
                                {
                                    Console.WriteLine(
                                        $"Chemical is removed so skipping : {chemical.Name} no {i} of {count}");
                                }
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"Chemical does not exist, so creating : {chemical.Name} no {i} of {count}");
                                await chemical.Create(chemicalsDbContext).ConfigureAwait(false);
                            }

                            i++;
                        });

                        var chemicalsDbContext = _chemicalDbContextHelper.GetDbContext();
                        var toBeRemoved = await chemicalsDbContext.Chemicals
                            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                            .Where(x => !regNos.Contains(x.RegistrationNo)).ToListAsync();

                        foreach (var chemical in toBeRemoved)
                        {
                            Console.WriteLine($@"Deleting chemical: {chemical.Name}");
                            await chemical.Delete(chemicalsDbContext);
                        }

                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                break;
            case 3:
            {
                Log.LogEvent("SearchListJob.Task: SearchListJob.Execute got called at 3am - Chemical entities");

                var sdkDbContext = _core.DbContextHelper.GetDbContext();
                var entityGroupBarcodeId =
                    await sdkDbContext.EntityGroups.FirstOrDefaultAsync(x => x.Name == "Chemicals - Barcode");
                var entityGroupRegNoId =
                    await sdkDbContext.EntityGroups.FirstOrDefaultAsync(x => x.Name == "Chemicals - RegNo");
                if (entityGroupBarcodeId != null && entityGroupRegNoId != null)
                {
                    var entityGroup = await _core.EntityGroupRead(entityGroupBarcodeId.MicrotingUid)
                        .ConfigureAwait(false);
                    var entityGroupRegNo = await _core
                        .EntityGroupRead(entityGroupRegNoId.MicrotingUid).ConfigureAwait(false);
                    var nextItemUid = entityGroup.EntityGroupItemLst.Count;
                    var _chemicalsDbContext = _chemicalDbContextHelper.GetDbContext();
                    var internalChemicals = await _chemicalsDbContext.Chemicals
                        // .Where(x => x.AuthorisationExpirationDate > DateTime.Now.AddYears(-10))
                        .Include(x => x.ClassificationAndLabeling)
                        .Include(x => x.ClassificationAndLabeling.CLP)
                        .Include(x => x.ClassificationAndLabeling.CLP.HazardStatements)
                        .Include(x => x.ClassificationAndLabeling.DPD)
                        .Include(x => x.AuthorisationHolder)
                        .Include(x => x.AuthorisationHolder.Address)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Include(x => x.Products).ToListAsync();


                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = -1
                    };
                    await Parallel.ForEachAsync(internalChemicals, parallelOptions, async (chemical, ct) =>
                    {
                        var internalDbContext = _core.DbContextHelper.GetDbContext();
                        if (!internalDbContext.EntityItems.AsNoTracking().Any(x =>
                                x.EntityGroupId == entityGroupRegNo.Id
                                && x.Name == chemical.RegistrationNo
                                && x.WorkflowState != Constants.WorkflowStates.Removed))
                        {
                            if (chemical.WorkflowState != Constants.WorkflowStates.Removed && chemical.Verified)
                            {
                                await _core.EntitySearchItemCreate(entityGroupRegNo.Id, chemical.RegistrationNo,
                                    chemical.Name,
                                    nextItemUid.ToString());
                                nextItemUid++;
                            }

                            Console.WriteLine(
                                $"Chemical does not exist, but it is removed, so skipping : {chemical.Name}");
                        }
                        else
                        {
                            if (chemical.WorkflowState == Constants.WorkflowStates.Removed)
                            {
                                Console.WriteLine(
                                    $"Chemical already exist, but is removed, so removing entity : {chemical.Name}");
                                var et = await sdkDbContext.EntityItems.FirstOrDefaultAsync(x =>
                                    x.EntityGroupId == entityGroupRegNo.Id && x.Name == chemical.RegistrationNo);
                                await _core.EntityItemDelete(et.Id);
                            }

                            Console.WriteLine($"Chemical already exist, so skipping : {chemical.Name}");
                        }
                    });
                }

                break;
            }
            case 4:
            {
                Log.LogEvent("SearchListJob.Task: SearchListJob.Execute got called at 4:00 - Chemicalss");
                var properties = await _backendConfigurationDbContext.Properties
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed).ToListAsync();

                var chemicalsDbContext = _chemicalDbContextHelper.GetDbContext();

                foreach (var property in properties)
                {

                    var propertyChemicals = await _backendConfigurationDbContext.ChemicalProductProperties
                        .Where(x => x.PropertyId == property.Id &&
                                    x.WorkflowState != Constants.WorkflowStates.Removed)
                        .ToListAsync();

                    foreach (var propertyChemical in propertyChemicals)
                    {
                        var chemical =
                            await chemicalsDbContext.Chemicals
                                .Include(x => x.ClassificationAndLabeling)
                                .Include(x => x.ClassificationAndLabeling.CLP)
                                .Include(x => x.ClassificationAndLabeling.CLP.HazardStatements)
                                .Include(x => x.ClassificationAndLabeling.DPD)
                                .Include(x => x.AuthorisationHolder)
                                .Include(x => x.AuthorisationHolder.Address)
                                .Include(x => x.Products)
                                .FirstAsync(x => x.Id == propertyChemical.ChemicalId);

                        if (propertyChemical != null)
                        {
                            string folderLookUpName = "25.07 Udløber om mere end 12 mdr.";
                            bool moveChemical = false;
                            propertyChemical.ExpireDate = chemical.UseAndPossesionDeadline ??
                                                          chemical.AuthorisationExpirationDate;
                            await propertyChemical.Update(_backendConfigurationDbContext);

                            if (propertyChemical.ExpireDate <= DateTime.UtcNow)
                            {
                                folderLookUpName = "25.02 Udløber i dag eller er udløbet";
                            }
                            else if (propertyChemical.ExpireDate <= DateTime.UtcNow.AddMonths(1))
                            {
                                folderLookUpName = "25.03 Udløber om senest 1 mdr.";
                            }
                            else if (propertyChemical.ExpireDate <= DateTime.UtcNow.AddMonths(3))
                            {
                                folderLookUpName = "25.04 Udløber om senest 3 mdr.";
                            }
                            else if (propertyChemical.ExpireDate <= DateTime.UtcNow.AddMonths(6))
                            {
                                folderLookUpName = "25.05 Udløber om senest 6 mdr.";
                            }
                            else if (propertyChemical.ExpireDate <= DateTime.UtcNow.AddMonths(12))
                            {
                                folderLookUpName = "25.06 Udløber om senest 12 mdr.";
                            }

                            if (propertyChemical.LastFolderName != folderLookUpName)
                            {
                                moveChemical = true;
                                propertyChemical.LastFolderName = folderLookUpName;
                                await propertyChemical.Update(_backendConfigurationDbContext);
                            }

                            if (moveChemical)
                            {
                                Console.WriteLine(
                                    $"Moving chemical with name : {chemical.Name} and registration no {chemical.RegistrationNo}");

                                var planningCaseSite =
                                    await _itemsPlanningPnDbContext.PlanningCaseSites.AsNoTracking()
                                        .FirstOrDefaultAsync(x =>
                                            x.MicrotingSdkCaseId == propertyChemical.SdkCaseId);

                                if (planningCaseSite == null)
                                {
                                    // var site = await sdkDbContext.Sites.SingleOrDefaultAsync(x => x.MicrotingUid == caseDto.SiteUId);
                                    var checkListSite = await _sdkDbContext.CheckListSites.AsNoTracking()
                                        .FirstOrDefaultAsync(x =>
                                            x.MicrotingUid == propertyChemical.SdkCaseId).ConfigureAwait(false);
                                    if (checkListSite == null)
                                    {
                                        return;
                                    }

                                    planningCaseSite =
                                        await _itemsPlanningPnDbContext.PlanningCaseSites.AsNoTracking()
                                            .FirstOrDefaultAsync(x =>
                                                x.MicrotingCheckListSitId == checkListSite.Id)
                                            .ConfigureAwait(false);
                                }

                                var planning =
                                    await _itemsPlanningPnDbContext.Plannings.AsNoTracking()
                                        .FirstAsync(x => x.Id == planningCaseSite.PlanningId);
                                var areaRulePlanning = await
                                    _backendConfigurationDbContext.AreaRulePlannings.FirstOrDefaultAsync(x =>
                                        x.ItemPlanningId == planning.Id);
                                var checkListTranslation = await _sdkDbContext.CheckListTranslations.FirstAsync(x =>
                                    x.Text == "25.02 Vis kemisk produkt");
                                var areaRule =
                                    await _backendConfigurationDbContext.AreaRules.Where(x =>
                                            x.Id == areaRulePlanning.AreaRuleId)
                                        .Include(x => x.Area)
                                        .Include(x => x.Property)
                                        .Include(x => x.AreaRuleTranslations)
                                        .FirstAsync();
                                var planningSites = await _itemsPlanningPnDbContext.PlanningSites
                                    .Where(x => x.PlanningId == planning.Id).ToListAsync();

                                var folder =
                                    await _sdkDbContext.Folders.FirstAsync(x => x.Id == areaRule.FolderId);
                                var folderTranslation = await _sdkDbContext.Folders.Join(
                                    _sdkDbContext.FolderTranslations,
                                    f => f.Id, translation => translation.FolderId, (f, translation) => new
                                    {
                                        f.Id,
                                        f.ParentId,
                                        translation.Name,
                                        f.MicrotingUid
                                    }).FirstAsync(x => x.Name == folderLookUpName && x.ParentId == folder.Id);
                                var folderMicrotingId = folderTranslation.MicrotingUid.ToString();

                                await _core.CaseDelete(propertyChemical.SdkCaseId);
                                await propertyChemical.Delete(_backendConfigurationDbContext);

                                var chemicalProductPropertySites =
                                    await _backendConfigurationDbContext.ChemicalProductPropertieSites
                                        .Where(x => x.PropertyId == areaRule.PropertyId)
                                        .Where(x => x.ChemicalId == chemical.Id)
                                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                        .ToListAsync();
                                foreach (var chemicalProductPropertySite in chemicalProductPropertySites)
                                {
                                    // var checkListSite = await sdkDbContext.CheckListSites.AsNoTracking().FirstAsync(x => x.Id == chemicalProductPropertySite.SdkCaseId);
                                    await _core.CaseDelete(chemicalProductPropertySite.SdkCaseId);
                                    await chemicalProductPropertySite.Delete(_backendConfigurationDbContext);
                                }

                                var productName = chemical.Name;

                                List<Microting.eForm.Dto.KeyValuePair> options =
                                    new List<Microting.eForm.Dto.KeyValuePair>();
                                int j = 0;
                                var totalLocations = string.Empty;
                                if (propertyChemical.Locations != null)
                                {
                                    foreach (var s in propertyChemical.Locations.Split("|"))
                                    {
                                        Microting.eForm.Dto.KeyValuePair keyValuePair =
                                            new Microting.eForm.Dto.KeyValuePair(j.ToString(), s, false,
                                                j.ToString());
                                        options.Add(keyValuePair);
                                        if (j != 0)
                                        {
                                            totalLocations += "|";
                                        }

                                        totalLocations += s;
                                        j++;
                                    }
                                }

                                var language =
                                    await _sdkDbContext.Languages.SingleAsync(x =>
                                        x.Id == propertyChemical.LanguageId);
                                var sdkSite = await _sdkDbContext.Sites.SingleAsync(x =>
                                    x.MicrotingUid == propertyChemical.SdkSiteId);
                                var product = await chemicalsDbContext.Products.FirstOrDefaultAsync(x =>
                                    x.ChemicalId == chemical.Id);


                                var mainElement = await _core.ReadeForm(checkListTranslation.CheckListId, language);
                                mainElement = await ModifyChemicalMainElement(mainElement, chemical, product,
                                    productName, folderMicrotingId, areaRule, sdkSite,
                                    totalLocations.Replace("|", ", "));

                                MultiSelect multiSelect = new MultiSelect(0, false, false,
                                    "Vælg rum som kemiproduktet skal fjernes fra", " ", Constants.FieldColors.Red,
                                    -1,
                                    false, options);
                                ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1])
                                    .DataItemList.Add(multiSelect);

                                if (string.IsNullOrEmpty(chemical.Use))
                                {
                                    ((DataElement) mainElement.ElementList[0]).DataItemGroupList
                                        .RemoveAt(0);
                                }

                                var caseId = await _core.CaseCreate(mainElement, "", (int) sdkSite.MicrotingUid,
                                    folder.Id);
                                var thisDbCase = await _sdkDbContext.CheckListSites.AsNoTracking()
                                    .FirstAsync(x => x.MicrotingUid == caseId);

                                var propertySites = await _backendConfigurationDbContext.PropertyWorkers
                                    .Where(x => x.PropertyId == areaRule.PropertyId).ToListAsync();

                                // This is repeated since now we are deploying the eForm to consumers and they should not see the remove product part.
                                if (string.IsNullOrEmpty(chemical.Use))
                                {
                                    ((DataElement) mainElement.ElementList[0]).DataItemGroupList
                                        .RemoveAt(0);
                                }
                                else
                                {
                                    ((DataElement) mainElement.ElementList[0]).DataItemGroupList
                                        .RemoveAt(1);
                                }

                                foreach (PropertyWorker propertyWorker in propertySites)
                                {
                                    if (propertyWorker.WorkerId != sdkSite.Id)
                                    {
                                        var site = await
                                            _sdkDbContext.Sites.SingleOrDefaultAsync(x =>
                                                x.Id == propertyWorker.WorkerId);
                                        var siteCaseId = await _core.CaseCreate(mainElement, "",
                                            (int) site!.MicrotingUid!,
                                            folder.Id);

                                        var chemicalProductPropertySite = new ChemicalProductPropertySite
                                        {
                                            ChemicalId = chemical.Id,
                                            SdkCaseId = (int) siteCaseId!,
                                            SdkSiteId = site!.Id,
                                            PropertyId = areaRule.PropertyId,
                                            LanguageId = language.Id
                                        };
                                        await chemicalProductPropertySite.Create(_backendConfigurationDbContext);
                                    }
                                }

                                AreaRulePlanningModel areaRulePlanningModel = new AreaRulePlanningModel
                                {
                                    Status = true,
                                    AssignedSites = new List<AreaRuleAssignedSitesModel>
                                    {
                                        new()
                                        {
                                            Checked = true,
                                            SiteId = sdkSite.Id
                                        }
                                    },
                                    SendNotifications = false,
                                    StartDate = DateTime.UtcNow,
                                    PropertyId = areaRule.PropertyId,
                                    ComplianceEnabled = false,
                                    TypeSpecificFields = null,
                                    RuleId = areaRule.Id
                                };

                                var newPlanning = await CreateItemPlanningObject(checkListTranslation.CheckListId,
                                    areaRule.EformName,
                                    areaRule.FolderId, areaRulePlanningModel, areaRule);
                                newPlanning.RepeatEvery = 0;
                                newPlanning.RepeatType = RepeatType.Day;
                                newPlanning.StartDate = DateTime.Now.ToUniversalTime();
                                var now = DateTime.UtcNow;
                                newPlanning.LastExecutedTime = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);
                                await newPlanning.Create(_itemsPlanningPnDbContext);
                                var newPlanningCase = new PlanningCase
                                {
                                    PlanningId = newPlanning.Id,
                                    Status = 66,
                                    MicrotingSdkeFormId = checkListTranslation.CheckListId
                                };
                                await newPlanningCase.Create(_itemsPlanningPnDbContext);
                                var newPlanningCaseSite = new PlanningCaseSite
                                {
                                    MicrotingSdkSiteId = sdkSite.Id,
                                    MicrotingSdkeFormId = checkListTranslation.CheckListId,
                                    Status = 66,
                                    PlanningId = newPlanning.Id,
                                    PlanningCaseId = newPlanningCase.Id,
                                    MicrotingSdkCaseId = (int) caseId,
                                    MicrotingCheckListSitId = thisDbCase.Id
                                };

                                await newPlanningCaseSite.Create(_itemsPlanningPnDbContext);

                                var newAreaRulePlanning = CreateAreaRulePlanningObject(areaRulePlanningModel,
                                    areaRule,
                                    newPlanning.Id,
                                    areaRule.FolderId);


                                await newAreaRulePlanning.Create(_backendConfigurationDbContext);
                                ChemicalProductProperty chemicalProductProperty = new ChemicalProductProperty
                                {
                                    ChemicalId = chemical.Id,
                                    PropertyId = areaRule.PropertyId,
                                    SdkCaseId = (int) caseId,
                                    Locations = totalLocations,
                                    LanguageId = language.Id,
                                    SdkSiteId = (int) sdkSite.MicrotingUid,
                                    ExpireDate = chemical.UseAndPossesionDeadline ??
                                                 chemical.AuthorisationExpirationDate
                                };

                                await chemicalProductProperty.Create(_backendConfigurationDbContext);
                            }
                        }
                    }

                    var sendGridKey =
                        _baseDbContext.ConfigurationValues.Single(x => x.Id == "EmailSettings:SendGridKey");

                    var fromEmailAddress = new EmailAddress("no-reply@microting.com",
                        $"KemiKontrol: {customerNo} {property.Name}");
                    var toEmailAddress = new List<EmailAddress>();
                    if (!string.IsNullOrEmpty(property.MainMailAddress))
                    {
                        toEmailAddress.AddRange(
                            property.MainMailAddress.Split(";").Select(s => new EmailAddress(s)));
                    }

                    if (toEmailAddress.Count > 0 && !string.IsNullOrEmpty(sendGridKey.Value))
                    {
                        var sendGridClient = new SendGridClient(sendGridKey.Value);
                        var assembly = Assembly.GetExecutingAssembly();
                        var assemblyName = assembly.GetName().Name;

                        var stream =
                            assembly.GetManifestResourceStream(
                                $"{assemblyName}.Resources.KemiKontrol_rapport_1.0_Libre.html");
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
                        newHtml = newHtml.Replace("{{dato}}", DateTime.Now.ToString("dd-MM-yyyy"));
                        newHtml = newHtml.Replace("{{emailaddresses}}", property.MainMailAddress);

                        var chemicals = await _backendConfigurationDbContext.ChemicalProductProperties.Where(x =>
                                x.WorkflowState != Constants.WorkflowStates.Removed && x.PropertyId == property.Id)
                            .OrderBy(x => x.ExpireDate).ToListAsync();
                        var expiredProducts = new List<ChemicalProductProperty>();
                        var expiringIn1Month = new List<ChemicalProductProperty>();
                        var expiringIn3Months = new List<ChemicalProductProperty>();
                        var expiringIn6Months = new List<ChemicalProductProperty>();
                        var expiringIn12Months = new List<ChemicalProductProperty>();
                        var otherProducts = new List<ChemicalProductProperty>();
                        var hasProducts = false;

                        foreach (ChemicalProductProperty chemicalProductProperty in chemicals)
                        {
                            var chemical =
                                chemicalsDbContext.Chemicals.Single(x =>
                                    x.Id == chemicalProductProperty.ChemicalId);
                            var expireDate = chemical.UseAndPossesionDeadline ??
                                             chemical.AuthorisationExpirationDate;
                            if (expireDate < DateTime.Now)
                            {
                                expiredProducts.Add(chemicalProductProperty);
                                hasProducts = true;
                            }
                            else if (expireDate < DateTime.Now.AddMonths(1))
                            {
                                expiringIn1Month.Add(chemicalProductProperty);
                                hasProducts = true;
                            }
                            else if (expireDate < DateTime.Now.AddMonths(3))
                            {
                                expiringIn3Months.Add(chemicalProductProperty);
                                hasProducts = true;
                            }
                            else if (expireDate < DateTime.Now.AddMonths(6))
                            {
                                expiringIn6Months.Add(chemicalProductProperty);
                                hasProducts = true;
                            }
                            else if (expireDate < DateTime.Now.AddMonths(12))
                            {
                                expiringIn12Months.Add(chemicalProductProperty);
                                hasProducts = true;
                            }
                            else
                            {
                                otherProducts.Add(chemicalProductProperty);
                                hasProducts = true;
                            }
                        }

                        if ((expiringIn1Month.Count > 0 && DateTime.Now.DayOfWeek == DayOfWeek.Thursday) ||
                            expiredProducts.Count > 0 ||
                            (DateTime.Now.DayOfWeek == DayOfWeek.Thursday && DateTime.Now.Day < 8 && hasProducts))
                        {

                            newHtml = newHtml.Replace("{{expiredProducts}}",
                                await GenerateProductList(expiredProducts, property, chemicalsDbContext));
                            newHtml = newHtml.Replace("{{expiringIn1Month}}",
                                await GenerateProductList(expiringIn1Month, property, chemicalsDbContext));
                            newHtml = newHtml.Replace("{{expiringIn3Months}}",
                                await GenerateProductList(expiringIn3Months, property, chemicalsDbContext));
                            newHtml = newHtml.Replace("{{expiringIn6Months}}",
                                await GenerateProductList(expiringIn6Months, property, chemicalsDbContext));
                            newHtml = newHtml.Replace("{{expiringIn12Months}}",
                                await GenerateProductList(expiringIn12Months, property, chemicalsDbContext));
                            newHtml = newHtml.Replace("{{otherProducts}}",
                                await GenerateProductList(otherProducts, property, chemicalsDbContext));

                            var msg = MailHelper.CreateSingleEmailToMultipleRecipients(fromEmailAddress,
                                toEmailAddress,
                                $"KemiKontrol: {customerNo} {property.Name}", null, newHtml);

                            List<Attachment> attachments = new List<Attachment>();

                            stream =
                                assembly.GetManifestResourceStream(
                                    $"{assemblyName}.Resources.KemiKontrol_rapport_1.0_Libre_html_5d7c0d01f9da8102.png");
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
                                Filename = "eform-logo.png",
                                Content = Convert.ToBase64String(bytes),
                                ContentId = "eform-logo",
                                Disposition = "inline"
                            };
                            attachments.Add(attachment1);

                            stream =
                                assembly.GetManifestResourceStream(
                                    $"{assemblyName}.Resources.KemiKontrol_rapport_1.0_Libre_html_36e139cc671b4deb.png");

                            if (stream == null)
                            {
                                throw new InvalidOperationException("Resource not found");
                            }

                            using (var memoryStream = new MemoryStream())
                            {
                                await stream.CopyToAsync(memoryStream);
                                bytes = memoryStream.ToArray();
                            }

                            var attachment2 = new Attachment
                            {
                                Filename = "back-arrow.png",
                                Content = Convert.ToBase64String(bytes),
                                ContentId = "back-arrow",
                                Disposition = "inline"
                            };
                            attachments.Add(attachment2);

                            stream =
                                assembly.GetManifestResourceStream(
                                    $"{assemblyName}.Resources.KemiKontrol_rapport_1.0_Libre_html_29bc21319b8001d7.png");

                            if (stream == null)
                            {
                                throw new InvalidOperationException("Resource not found");
                            }

                            using (var memoryStream = new MemoryStream())
                            {
                                await stream.CopyToAsync(memoryStream);
                                bytes = memoryStream.ToArray();
                            }

                            var attachment3 = new Attachment
                            {
                                Filename = "sync-button.png",
                                Content = Convert.ToBase64String(bytes),
                                ContentId = "sync-button",
                                Disposition = "inline"
                            };
                            attachments.Add(attachment3);
                            msg.AddAttachments(attachments);

                            var responseMessage = await sendGridClient.SendEmailAsync(msg);
                            if ((int) responseMessage.StatusCode < 200 ||
                                (int) responseMessage.StatusCode >= 300)
                            {
                                throw new Exception($"Status: {responseMessage.StatusCode}");
                            }
                        }
                    }
                }

                break;
            }
            case 18:
            {

                var brokenPlannings = await _itemsPlanningPnDbContext.Plannings
                    .Where(x => x.ShowExpireDate == false)
                    .Where(x => x.Enabled)
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed).ToListAsync();
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
                    foreach (var brokenPlanning in brokenPlannings)
                    {
                        try
                        {
                            var planningTranslation = await _itemsPlanningPnDbContext.PlanningNameTranslation
                                .FirstAsync(x => x.PlanningId == brokenPlanning.Id);
                            stringBuilder.Append(
                                $"<p>Planning with id: {brokenPlanning.Id} and name: {planningTranslation.Name} has ShowExpireDate set to false</p>");
                            await brokenPlanning.Delete(_itemsPlanningPnDbContext).ConfigureAwait(false);
                        }
                        catch
                        {
                            await brokenPlanning.Delete(_itemsPlanningPnDbContext).ConfigureAwait(false);
                            var planningCaseSite = await _itemsPlanningPnDbContext.PlanningCaseSites
                                .FirstOrDefaultAsync(x => x.PlanningId == brokenPlanning.Id);
                            if (planningCaseSite != null)
                            {
                                var planningSites = await _itemsPlanningPnDbContext.PlanningSites
                                    .Where(x => x.PlanningId == brokenPlanning.Id).ToListAsync();
                                if (!planningSites.Any())
                                {
                                    var planningCases = await _itemsPlanningPnDbContext.PlanningCases
                                        .Where(x => x.PlanningId == brokenPlanning.Id).ToListAsync();
                                    if (!planningCases.Any())
                                    {

                                    }
                                }
                            }
                            stringBuilder.Append(
                                $"<p>Planning with id: {brokenPlanning.Id} has ShowExpireDate set to false</p>");
                        }
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
                break;
            case 5:
            {
                /* Find all compliances which have expired today and we have call sdk and move the eform from the current folder to the expired folder
                 * also we need to set the ignore_end_date, when doing the call.
                 * The expired folder is found by looking at the area rule -> area -> expired folder
                 */
                var complianceMovementIsEnabled = await _backendConfigurationDbContext.PluginConfigurationValues
                    .FirstOrDefaultAsync(x => x.Name == "BackendConfigurationSettings:ComplianceOverdueMovementEnabled");

                if (complianceMovementIsEnabled == null || !bool.TryParse(complianceMovementIsEnabled.Value, out var isEnabled) || !isEnabled)
                {
                    Log.LogEvent("SearchListJob.Task: Compliance overdue movement is disabled. Exiting.");
                    break;
                }


                var changeDate = DateTime.Parse("2025-10-30 00:00:00");

                var complianceList = await _backendConfigurationDbContext.Compliances
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.Deadline < DateTime.UtcNow)
                    .Where(x => x.Deadline > changeDate)
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
                    Console.WriteLine($"Moved compliance case with id: {theCase.Id} to overdue folder");
                    var comp = await _backendConfigurationDbContext.Compliances.FirstAsync(x => x.Id == compliance.Id);
                    await comp.Delete(_backendConfigurationDbContext).ConfigureAwait(false);
                }
                break;
            }
            case 8:
            {
                Log.LogEvent("SearchListJob.Task: SearchListJob.Execute got called at 8:00 UTC - Opgavestatus");
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

                            Log.LogEvent("Opgavestatus. Found " + complianceList.Count + " compliances for property: " +
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
                            Console.WriteLine(e);
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
            Console.WriteLine(e);
        }
    }

    private async Task<MainElement> ModifyChemicalMainElement(MainElement mainElement, Chemical chemical,
        Product product, string productName, string folderMicrotingId, AreaRule areaRule, Site sdkSite,
        string locations)
    {
        mainElement.Repeated = 0;
        mainElement.CheckListFolderName = folderMicrotingId;
        mainElement.StartDate = DateTime.Now.ToUniversalTime();
        mainElement.EndDate = DateTime.Now.AddYears(10).ToUniversalTime();
        mainElement.DisplayOrder = 10000000;
        mainElement.ElementList[0].DoneButtonEnabled = false;
        mainElement.Label = productName;
        mainElement.ElementList[0].Label = productName;
        mainElement.ElementList.First().Description.InderValue =
            $"{chemical.AuthorisationHolder.Name}<br>" +
            $"Reg nr.: {chemical.RegistrationNo}<br>";
        if (chemical.PesticideProductGroup.Count > 0)
        {
            mainElement.ElementList.First().Description.InderValue += "Produktgruppe: ";
            var n = 0;
            foreach (int i in chemical.PesticideProductGroup)
            {
                if (n > 0)
                {
                    mainElement.ElementList.First().Description.InderValue += ",";
                }

                mainElement.ElementList.First().Description.InderValue += Microting.EformBackendConfigurationBase
                    .Infrastructure.Const.Constants.ProductGroupPesticide.First(x => x.Key == i).Value;
                n++;
            }

            mainElement.ElementList.First().Description.InderValue += "<br><br>";
        }

        mainElement.ElementList.First().Description.InderValue +=
            $"<strong>Placering</strong><br>Ejendom: {areaRule.Property.Name}<br>Rum: {locations}<br><br><strong>Udløbsdato: </strong><br>";

        if (chemical.UseAndPossesionDeadline != null)
        {
            mainElement.ElementList.First().Description.InderValue +=
                $"Dato: {chemical.UseAndPossesionDeadline:dd-MM-yyyy}<br><br>";
        }
        else
        {
            mainElement.ElementList.First().Description.InderValue +=
                $"Dato: {chemical.AuthorisationExpirationDate:dd-MM-yyyy}<br><br>";
        }

        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Label = productName;
        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue =
            $"{chemical.AuthorisationHolder.Name}<br>" +
            $"Reg nr.: {chemical.RegistrationNo}<br>";

        if (chemical.PesticideProductGroup.Count > 0)
        {
            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue += "Produktgruppe: ";
            var n = 0;
            foreach (int i in chemical.PesticideProductGroup)
            {
                if (n > 0)
                {
                    ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                        .InderValue += ",";
                }

                ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                    .InderValue += Microting.EformBackendConfigurationBase
                        .Infrastructure.Const.Constants.ProductGroupPesticide.First(x => x.Key == i).Value;
                n++;
            }

            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue += "<br><br>";
        }

        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue +=
            "<strong>Udløbsdato</strong><br>";

        if (chemical.UseAndPossesionDeadline != null)
        {
            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue += $"Dato: {chemical.UseAndPossesionDeadline:dd-MM-yyyy}<br><br>";
        }
        else
        {
            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue += $"Dato: {chemical.AuthorisationExpirationDate:dd-MM-yyyy}<br><br>";
        }

        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue +=
            "<strong>Placering:</strong><br>" +
            $"Ejendom: {areaRule.Property.Name}<br>" +
            $"Rum: {locations}<br><br>" +
            "<strong>Klassificering og mærkening</strong><br>";
        List<string> HStatements = new List<string>();
        foreach (var hazardStatement in chemical.ClassificationAndLabeling.CLP.HazardStatements)
        {
            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue +=
                $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.HazardStatement.First(x => x.Key == hazardStatement.Statement).Value}<br><br>";
            Regex regex = new Regex(@"\((H\d\d\d)\)");
            var res = regex.Match(Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants
                .HazardStatement.First(x => x.Key == hazardStatement.Statement).Value);
            HStatements.Add(res.Value);
        }

        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue +=
            "<br><strong>Generelle oplysninger</strong><br>" +
            "<u>Bekæmpelsesmiddelstype</u><br>";

        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue += chemical.PestControlType != null
                ? $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.PestControlType.FirstOrDefault(x => x.Key == chemical.PestControlType)!.Value}<br><br>"
                : "";

        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue +=
            "<br><u>Productstatus</u><br>" +
            $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.ProductStatusType.FirstOrDefault(x => x.Key == chemical.Status).Value}<br><br>" +
            $"<u>Pesticid produktgruppe</u><br>";
        foreach (var i in chemical.PesticideProductGroup)
        {
            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue +=
                $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.ProductGroupPesticide.FirstOrDefault(x => x.Key == i).Value}<br>";
        }

        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue +=
            "<br><u>Godkendelsesdato</u><br>" +
            $"{chemical.AuthorisationDate:dd-MM-yyyy}<br><br>";

        ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[0])
            .DataItemList[0].Label = " ";
        ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[0])
            .DataItemList[0].Description
            .InderValue =
            $"{chemical.Use}<br>";

        ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1])
            .DataItemList[0].Label = "Kemiprodukt fjernet";
        ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1].Label = "Hvordan fjerner jeg et produkt?";
        string description = $"Produkt: {productName}<br>" +
                             $"Producent: {chemical.AuthorisationHolder.Name}<br>" +
                             $"Reg nr.: {chemical.RegistrationNo}<br>";
        if (chemical.PesticideProductGroup.Count > 0)
        {
            description += "Produktgruppe: ";
            var n = 0;
            foreach (int i in chemical.PesticideProductGroup)
            {
                if (n > 0)
                {
                    description += ",";
                }

                description += Microting.EformBackendConfigurationBase
                    .Infrastructure.Const.Constants.ProductGroupPesticide.First(x => x.Key == i).Value;
                n++;
            }

            description += "<br><br>";
        }

        description += $"Ejendom: {areaRule.Property.Name}<br>" +
                       $"Rum: {locations}<br><br>" +
                       "<strong>Gør følgende for at fjerne et produkt:</strong><br>" +
                       "1. Vælg hvilke rum produktet skal fjernes fra og tryk Gem.<br>" +
                       "2. Sæt flueben i <strong>Produkt fjernet</strong><br>" +
                       "3. Tryk på Bekræft<br><br>";
        None none = new None(1, false, false, " ", description, Constants.FieldColors.Red, -2, false);
        ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1]).DataItemList.Add(none);
        ((CheckBox) ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1])
                .DataItemList[0]).Label =
            "Produkt fjernet";
        ((SaveButton) ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1])
                .DataItemList[1]).Label =
            "Bekræft produkt fjernet";
        if (product != null)
        {
            if (!string.IsNullOrEmpty(product.FileName))
            {
                using var webClient = new HttpClient();

                await using (var s = await webClient.GetStreamAsync(
                                 $"https://chemicalbase.microting.com/api/chemicals-pn/get-pdf-file?fileName={product.FileName}"))
                {
                    File.Delete(Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"));
                    await using (var fs = new FileStream(
                                     Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"),
                                     FileMode.CreateNew))
                    {
                        await s.CopyToAsync(fs);
                    }
                }

                var pdfId = await _core.PdfUpload(Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"));

                await _core.PutFileToStorageSystem(Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"),
                    $"{product.FileName}.pdf");
                File.Delete(Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"));

                ((ShowPdf) ((DataElement) mainElement.ElementList[0]).DataItemList[1]).Value = pdfId;
                ((DataElement) mainElement.ElementList[0]).DataItemList.RemoveAt(2);
            }
            else
            {
                ((DataElement) mainElement.ElementList[0]).DataItemList.RemoveAt(1);
                ((DataElement) mainElement.ElementList[0]).DataItemList.RemoveAt(1);
            }
        }
        else
        {
            ((DataElement) mainElement.ElementList[0]).DataItemList.RemoveAt(1);
            ((DataElement) mainElement.ElementList[0]).DataItemList.RemoveAt(1);
        }

        return mainElement;
    }

    private async Task<Planning> CreateItemPlanningObject(int eformId, string eformName, int folderId,
        AreaRulePlanningModel areaRulePlanningModel, AreaRule areaRule)
    {
        // var _backendConfigurationPnDbContext = BackendConfigurationDbContextHelper.GetDbContext();
        var propertyItemPlanningTagId = await _backendConfigurationDbContext.Properties
            .Where(x => x.Id == areaRule.PropertyId)
            .Select(x => x.ItemPlanningTagId)
            .FirstAsync();
        return new Planning
        {
            CreatedByUserId = 0,
            Enabled = areaRulePlanningModel.Status,
            RelatedEFormId = eformId,
            RelatedEFormName = eformName,
            SdkFolderId = folderId,
            DaysBeforeRedeploymentPushMessageRepeat = false,
            DaysBeforeRedeploymentPushMessage = 5,
            PushMessageOnDeployment = areaRulePlanningModel.SendNotifications,
            StartDate = areaRulePlanningModel.StartDate,
            IsLocked = true,
            IsEditable = false,
            IsHidden = true,
            PlanningSites = areaRulePlanningModel.AssignedSites
                .Select(x =>
                    new Microting.ItemsPlanningBase.Infrastructure.Data.Entities.PlanningSite
                    {
                        SiteId = x.SiteId
                    })
                .ToList(),
            PlanningsTags = new List<PlanningsTags>
            {
                new() {PlanningTagId = areaRule.Area.ItemPlanningTagId},
                new() {PlanningTagId = propertyItemPlanningTagId}
            }
        };
    }

    private AreaRulePlanning CreateAreaRulePlanningObject(AreaRulePlanningModel areaRulePlanningModel,
        AreaRule areaRule, int planningId, int folderId)
    {
        var areaRulePlanning = new AreaRulePlanning
        {
            AreaId = areaRule.AreaId,
            CreatedByUserId = 0,
            UpdatedByUserId = 0,
            StartDate = areaRulePlanningModel.StartDate,
            Status = areaRulePlanningModel.Status,
            SendNotifications = areaRulePlanningModel.SendNotifications,
            AreaRuleId = areaRulePlanningModel.RuleId,
            ItemPlanningId = planningId,
            FolderId = folderId,
            PropertyId = areaRulePlanningModel.PropertyId,
            PlanningSites = areaRulePlanningModel.AssignedSites.Select(x => new PlanningSite
            {
                SiteId = x.SiteId,
                CreatedByUserId = 0,
                UpdatedByUserId = 0,
                AreaId = areaRule.AreaId,
                AreaRuleId = areaRule.Id
            }).ToList(),
            ComplianceEnabled = areaRulePlanningModel.ComplianceEnabled
        };
        if (areaRulePlanningModel.TypeSpecificFields != null)
        {
            areaRulePlanning.DayOfMonth = areaRulePlanningModel.TypeSpecificFields.DayOfMonth == 0
                ? 1
                : areaRulePlanningModel.TypeSpecificFields.DayOfMonth;
            areaRulePlanning.DayOfWeek = areaRulePlanningModel.TypeSpecificFields.DayOfWeek == 0
                ? 1
                : areaRulePlanningModel.TypeSpecificFields.DayOfWeek;
            areaRulePlanning.HoursAndEnergyEnabled = areaRulePlanningModel.TypeSpecificFields.HoursAndEnergyEnabled;
            areaRulePlanning.EndDate = areaRulePlanningModel.TypeSpecificFields.EndDate;
            areaRulePlanning.RepeatEvery = areaRulePlanningModel.TypeSpecificFields.RepeatEvery;
            areaRulePlanning.RepeatType = areaRulePlanningModel.TypeSpecificFields.RepeatType;
        }

        if (areaRule.Type != null)
        {
            areaRulePlanning.Type = (AreaRuleT2TypesEnum) areaRule.Type;
        }

        if (areaRule.Alarm != null)
        {
            areaRulePlanning.Alarm = (AreaRuleT2AlarmsEnum) areaRule.Alarm;
        }

        return areaRulePlanning;
    }

    private async Task<string> GenerateProductList(List<ChemicalProductProperty> chemicalProductProperties,
        Property property, ChemicalsDbContext dbContext)
    {
        string result = "";
        foreach (var chemicalProductProperty in chemicalProductProperties.OrderBy(x => x.ExpireDate))
        {
            var chemical = await dbContext.Chemicals
                .Include(x => x.AuthorisationHolder)
                .FirstAsync(x => x.Id == chemicalProductProperty.ChemicalId);

            var productGroups = "";
            int j = 0;
            foreach (var i in chemical.PesticideProductGroup)
            {
                if (j == 0)
                {
                    productGroups +=
                        $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.ProductGroupPesticide.FirstOrDefault(x => x.Key == i).Value}";
                    ;
                }
                else
                {
                    productGroups += ", " +
                                     $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.ProductGroupPesticide.FirstOrDefault(x => x.Key == i).Value}";
                    ;
                }

                j++;
            }

            var expireDate = "";
            if (chemical.UseAndPossesionDeadline != null)
            {
                var bla = (DateTime) chemical.UseAndPossesionDeadline;
                expireDate = bla.ToString("dd-MM-yyyy");
            }
            else
            {
                var bla = (DateTime) chemical.AuthorisationExpirationDate!;
                expireDate = bla.ToString("dd-MM-yyyy");
            }

            result += "<tr valign=\"top\">" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{chemical.Name}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{chemical.AuthorisationHolder.Name}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{chemical.RegistrationNo}</span></p>" +
                      "</td><td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{productGroups}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{expireDate}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{property.Name}</span></p>" +
                      "</td>" +
                      "<td width=\"99\"" +
                      "style=\"border-left: 1px solid #000000; border-right: 1px solid #000000; border-bottom: 1px solid #000000;  padding: 0 0.08in\">" +
                      "<p align=\"left\" style=\"orphans: 2; widows: 2\">" +
                      $"<span>{chemicalProductProperty.Locations}</span></p>" +
                      "</td>" +
                      "</tr>";
        }

        return result;
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