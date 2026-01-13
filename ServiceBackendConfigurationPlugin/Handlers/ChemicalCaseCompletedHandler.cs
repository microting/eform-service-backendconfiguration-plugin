using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Models;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using Rebus.Handlers;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Infrastructure.Models.AreaRules;
using ServiceBackendConfigurationPlugin.Messages;
using File = System.IO.File;
using PlanningSite = Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.PlanningSite;

namespace ServiceBackendConfigurationPlugin.Handlers;

public class ChemicalCaseCompletedHandler(
    ChemicalDbContextHelper chemicalDbContextHelper,
    BackendConfigurationDbContextHelper backendConfigurationDbContextHelper,
    ItemsPlanningDbContextHelper itemsPlanningDbContextHelper,
    eFormCore.Core sdkCore)
    : IHandleMessages<ChemicalCaseCompleted>
{
    public async Task Handle(ChemicalCaseCompleted message)
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


        var areaRulePlanning = await
            backendConfigurationPnDbContext.AreaRulePlannings.FirstOrDefaultAsync(x =>
                x.ItemPlanningId == planning.Id);
        var checkListTranslation = await sdkDbContext.CheckListTranslations.FirstAsync(x =>
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
        var planningSites = await itemsPlanningPnDbContext.PlanningSites
            .Where(x => x.PlanningId == planning.Id).ToListAsync();

        var sdkSite = await sdkDbContext.Sites.FirstAsync(x => x.Id == planningSites.First().SiteId);
        var language = await sdkDbContext.Languages.FirstAsync(x => x.Id == sdkSite.LanguageId);
        var caseIds = new List<int> {dbCase.Id};
        var fieldValues = await sdkCore.Advanced_FieldValueReadList(caseIds, language);
        var chemicalDbContext = chemicalDbContextHelper.GetDbContext();
        var folder = await sdkDbContext.Folders.FirstAsync(x => x.Id == areaRule.FolderId);

        if (planningCaseSite.MicrotingSdkeFormId == checkListTranslation.CheckListId)
        {

            List<string> entityIds = fieldValues
                .Where(x => x.Value != "null" && x.Value != null && x.FieldType == Constants.FieldTypes.EntitySearch)
                .Select(x => x.Value).ToList();
            var location =
                fieldValues.FirstOrDefault(x =>
                    x.ValueReadable != "null" && x.ValueReadable != null &&
                    x.FieldType == Constants.FieldTypes.EntitySelect);
            // .Select(x => x.ValueReadable).ToList();


            // List<string> barcodes = new List<string>();
            // List<string> regNos = new List<string>();

            List<Chemical> chemicals = new List<Chemical>();

            checkListTranslation = await sdkDbContext.CheckListTranslations.FirstAsync(x =>
                x.Text == "25.02 Vis kemisk produkt");

            foreach (string entityId in entityIds)
            {
                var entityItem = await sdkDbContext.EntityItems.FirstAsync(x => x.Id == int.Parse(entityId));
                Chemical chemical = null;
                Product product = null;
                if (entityItem.Name.Contains("-"))
                {
                    chemical =
                        await chemicalDbContext.Chemicals
                            .Include(x => x.ClassificationAndLabeling)
                            .Include(x => x.ClassificationAndLabeling.CLP)
                            .Include(x => x.ClassificationAndLabeling.CLP.HazardStatements)
                            .Include(x => x.ClassificationAndLabeling.DPD)
                            .Include(x => x.AuthorisationHolder)
                            .Include(x => x.AuthorisationHolder.Address)
                            // .Include(x=> x.ClassificationAndLabeling.DPD.RiskPhrases)
                            .FirstAsync(x => x.RegistrationNo == entityItem.Name);
                    product = await chemicalDbContext.Products.FirstOrDefaultAsync(x =>
                        x.ChemicalId == chemical.Id);
                    // chemicals.Add(chemical);
                    // regNos.Add(entityItem.Name);
                }
                else
                {
                    product =
                        await chemicalDbContext.Products.FirstOrDefaultAsync(x =>
                            x.Barcode == entityItem.Name && x.WorkflowState != Constants.WorkflowStates.Removed);
                    chemical = await chemicalDbContext.Chemicals
                        .Include(x => x.ClassificationAndLabeling)
                        .Include(x => x.ClassificationAndLabeling.CLP)
                        .Include(x => x.ClassificationAndLabeling.CLP.HazardStatements)
                        .Include(x => x.ClassificationAndLabeling.DPD)
                        // .Include(x=> x.ClassificationAndLabeling.DPD.RiskPhrases)
                        .FirstAsync(x => x.Id == product.ChemicalId);
                    // chemicals.Add(chemical);
                }

                string folderLookUpName = "25.07 Udløber om mere end 12 mdr.";
                var expireDate = chemical.UseAndPossesionDeadline ?? chemical.AuthorisationExpirationDate;
                if (expireDate <= DateTime.UtcNow)
                {
                    folderLookUpName = "25.02 Udløber i dag eller er udløbet";
                }
                else if (expireDate <= DateTime.UtcNow.AddMonths(1))
                {
                    folderLookUpName = "25.03 Udløber om senest 1 mdr.";
                }
                else if (expireDate <= DateTime.UtcNow.AddMonths(3))
                {
                    folderLookUpName = "25.04 Udløber om senest 3 mdr.";
                }
                else if (expireDate <= DateTime.UtcNow.AddMonths(6))
                {
                    folderLookUpName = "25.05 Udløber om senest 6 mdr.";
                }
                else if (expireDate <= DateTime.UtcNow.AddMonths(12))
                {
                    folderLookUpName = "25.06 Udløber om senest 12 mdr.";
                }

                var folderTranslation = await sdkDbContext.Folders.Join(sdkDbContext.FolderTranslations,
                    f => f.Id, translation => translation.FolderId, (f, translation) => new
                    {
                        f.Id,
                        f.ParentId,
                        translation.Name,
                        f.MicrotingUid
                    }).FirstAsync(x => x.Name == folderLookUpName && x.ParentId == folder.Id);
                var folderMicrotingId = folderTranslation.MicrotingUid.ToString();

                if (!backendConfigurationPnDbContext.ChemicalProductProperties.Any(x =>
                        x.ChemicalId == chemical.Id
                        && x.WorkflowState != Constants.WorkflowStates.Removed
                        && x.Locations.Contains(location.ValueReadable)
                        && x.PropertyId == areaRule.PropertyId))
                {
                    var currentDeployment = await backendConfigurationPnDbContext.ChemicalProductProperties
                        .FirstOrDefaultAsync(x =>
                            x.ChemicalId == chemical.Id &&
                            x.WorkflowState != Constants.WorkflowStates.Removed
                            && x.PropertyId == areaRule.PropertyId);
                    if (currentDeployment != null)
                    {
                        // var theCheckListSite = await sdkDbContext.CheckListSites.AsNoTracking().FirstAsync(x => x.Id == currentDeployment.SdkCaseId);
                        await sdkCore.CaseDelete(currentDeployment.SdkCaseId);
                        await currentDeployment!.Delete(backendConfigurationPnDbContext);
                    }

                    var chemicalProductPropertySites =
                        await backendConfigurationPnDbContext.ChemicalProductPropertieSites
                            .Where(x => x.PropertyId == areaRule.PropertyId)
                            .Where(x => x.ChemicalId == chemical.Id)
                            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed).ToListAsync();
                    foreach (var chemicalProductPropertySite in chemicalProductPropertySites)
                    {
                        // var checkListSite = await sdkDbContext.CheckListSites.AsNoTracking().FirstAsync(x => x.Id == chemicalProductPropertySite.SdkCaseId);
                        await sdkCore.CaseDelete(chemicalProductPropertySite.SdkCaseId);
                        await chemicalProductPropertySite.Delete(backendConfigurationPnDbContext);
                    }

                    var productName = chemical.Name;
                    if (product != null)
                    {
                        if (product.Name != "Emballagestørrelse ikke angivet")
                        {
                            productName += " - " + product.Name;
                        }
                    }

                    List<Microting.eForm.Dto.KeyValuePair> options =
                        new List<Microting.eForm.Dto.KeyValuePair>();
                    int j = 0;
                    var totalLocations = string.Empty;
                    foreach (var s in location!.ValueReadable.Split("|"))
                    {
                        Microting.eForm.Dto.KeyValuePair keyValuePair =
                            new Microting.eForm.Dto.KeyValuePair(j.ToString(), s, false, j.ToString());
                        options.Add(keyValuePair);
                        totalLocations = s;
                        j++;
                    }

                    if (currentDeployment != null)
                    {
                        foreach (var s in currentDeployment.Locations.Split("|"))
                        {
                            Microting.eForm.Dto.KeyValuePair keyValuePair =
                                new Microting.eForm.Dto.KeyValuePair(j.ToString(), s, false, j.ToString());
                            options.Add(keyValuePair);
                            totalLocations += "|" + s;
                            j++;
                        }
                    }

                    var mainElement = await sdkCore.ReadeForm(checkListTranslation.CheckListId, language);
                    mainElement = await ModifyChemicalMainElement(mainElement, chemical, product,
                            productName, folderMicrotingId, areaRule, sdkSite, totalLocations.Replace("|", ", "))
                        .ConfigureAwait(false);

                    MultiSelect multiSelect = new MultiSelect(0, false, false,
                        "Vælg rum som produktet er fjernet fra", " ", Constants.FieldColors.Red, -1,
                        false, options);
                    ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1]).DataItemList.Add(
                        multiSelect);

                    if (string.IsNullOrEmpty(chemical.Use))
                    {
                        ((DataElement) mainElement.ElementList[0]).DataItemGroupList
                            .RemoveAt(0);
                    }

                    var caseId = await sdkCore.CaseCreate(mainElement, "", (int) sdkSite.MicrotingUid,
                        folder.Id);
                    var thisDbCase = await sdkDbContext.CheckListSites.AsNoTracking()
                        .FirstAsync(x => x.MicrotingUid == caseId);

                    var propertySites = await backendConfigurationPnDbContext.PropertyWorkers
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
                                sdkDbContext.Sites.FirstOrDefaultAsync(x => x.Id == propertyWorker.WorkerId);
                            var siteCaseId = await sdkCore.CaseCreate(mainElement, "", (int) site!.MicrotingUid!,
                                folder.Id);
                            var chemicalProductPropertySite = new ChemicalProductPropertySite
                            {
                                ChemicalId = chemical.Id,
                                SdkCaseId = (int) siteCaseId!,
                                SdkSiteId = site!.Id,
                                PropertyId = areaRule.PropertyId
                            };
                            await chemicalProductPropertySite.Create(backendConfigurationPnDbContext);
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
                    await newPlanning.Create(itemsPlanningPnDbContext);
                    var newPlanningCase = new PlanningCase
                    {
                        PlanningId = newPlanning.Id,
                        Status = 66,
                        MicrotingSdkeFormId = checkListTranslation.CheckListId
                    };
                    await newPlanningCase.Create(itemsPlanningPnDbContext);
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

                    await newPlanningCaseSite.Create(itemsPlanningPnDbContext);

                    var newAreaRulePlanning = CreateAreaRulePlanningObject(areaRulePlanningModel, areaRule,
                        newPlanning.Id,
                        areaRule.FolderId);


                    await newAreaRulePlanning.Create(backendConfigurationPnDbContext);
                    ChemicalProductProperty chemicalProductProperty = new ChemicalProductProperty
                    {
                        ChemicalId = chemical.Id,
                        PropertyId = areaRule.PropertyId,
                        SdkCaseId = (int) caseId,
                        Locations = totalLocations,
                        LanguageId = language.Id,
                        SdkSiteId = (int) sdkSite.MicrotingUid,
                        ExpireDate = chemical.UseAndPossesionDeadline ?? chemical.AuthorisationExpirationDate
                    };

                    await chemicalProductProperty.Create(backendConfigurationPnDbContext);
                }
            }
        }
        else
        {
            checkListTranslation = await sdkDbContext.CheckListTranslations.FirstAsync(x =>
                x.Text == "25.02 Vis kemisk produkt");
            if (planningCaseSite.MicrotingSdkeFormId == checkListTranslation.CheckListId)
            {
                if (fieldValues.Any(x =>
                        x.FieldType == Constants.FieldTypes.CheckBox && x.Value == "checked"))
                {
                    var indexesToRemove = fieldValues.First(x => x.FieldId == 0).Value.Split("|").ToList();
                    var cpp = await backendConfigurationPnDbContext.ChemicalProductProperties
                        .FirstAsync(x =>
                            x.SdkCaseId == dbCase.MicrotingUid);
                    var currentLocations = cpp.Locations.Split("|").ToList();
                    var locationsToBeRemoved = new List<string>();

                    if (currentLocations.Count == indexesToRemove.Count)
                    {
                        cpp.Locations = "";
                    }
                    else
                    {

                        foreach (var index in indexesToRemove)
                        {
                            var location = currentLocations[int.Parse(index)];
                            locationsToBeRemoved.Add(location);
                        }

                        foreach (var s in locationsToBeRemoved)
                        {
                            cpp.Locations = cpp.Locations.Replace(s, "").Replace("||", "|");
                        }

                        if (cpp.Locations.StartsWith("|"))
                        {
                            cpp.Locations = cpp.Locations.Substring(1, cpp.Locations.Length - 1);
                        }

                        if (cpp.Locations.EndsWith("|"))
                        {
                            cpp.Locations = cpp.Locations.Substring(0, cpp.Locations.Length - 1);
                        }

                    }

                    if (cpp.Locations == "")
                    {
                        await sdkCore.CaseDelete(cpp.SdkCaseId);
                        await cpp.Delete(backendConfigurationPnDbContext);
                    }
                    else
                    {
                        await sdkCore.CaseDelete(cpp.SdkCaseId);
                        await cpp.Delete(backendConfigurationPnDbContext);
                        var chemical = await chemicalDbContext.Chemicals
                            .Include(x => x.ClassificationAndLabeling)
                            .Include(x => x.ClassificationAndLabeling.CLP)
                            .Include(x => x.ClassificationAndLabeling.CLP.HazardStatements)
                            .Include(x => x.ClassificationAndLabeling.DPD)
                            .Include(x => x.AuthorisationHolder)
                            .Include(x => x.AuthorisationHolder.Address)
                            // .Include(x=> x.ClassificationAndLabeling.DPD.RiskPhrases)
                            .FirstAsync(x => x.Id == cpp.ChemicalId);
                        var product = await chemicalDbContext.Products.FirstOrDefaultAsync(x =>
                            x.ChemicalId == chemical.Id);
                        var productName = chemical.Name;
                        if (product != null)
                        {
                            if (product!.Name != "Emballagestørrelse ikke angivet")
                            {
                                productName += " - " + product!.Name;
                            }
                        }

                        string folderLookUpName = "25.07 Udløber om mere end 12 mdr.";
                        var expireDate = chemical.UseAndPossesionDeadline ?? chemical.AuthorisationExpirationDate;
                        if (expireDate <= DateTime.UtcNow)
                        {
                            folderLookUpName = "25.02 Udløber i dag eller er udløbet";
                        }
                        else if (expireDate <= DateTime.UtcNow.AddMonths(1))
                        {
                            folderLookUpName = "25.03 Udløber om senest 1 mdr.";
                        }
                        else if (expireDate <= DateTime.UtcNow.AddMonths(3))
                        {
                            folderLookUpName = "25.04 Udløber om senest 3 mdr.";
                        }
                        else if (expireDate <= DateTime.UtcNow.AddMonths(6))
                        {
                            folderLookUpName = "25.05 Udløber om senest 6 mdr.";
                        }
                        else if (expireDate <= DateTime.UtcNow.AddMonths(12))
                        {
                            folderLookUpName = "25.06 Udløber om senest 12 mdr.";
                        }

                        var folderTranslation = await sdkDbContext.Folders.Join(sdkDbContext.FolderTranslations,
                            f => f.Id, translation => translation.FolderId, (f, translation) => new
                            {
                                f.Id,
                                f.ParentId,
                                translation.Name,
                                f.MicrotingUid
                            }).FirstAsync(x => x.Name == folderLookUpName && x.ParentId == folder.Id);
                        var folderMicrotingId = folderTranslation.MicrotingUid.ToString();
                        var mainElement = await sdkCore.ReadeForm(checkListTranslation.CheckListId, language);
                        mainElement = await ModifyChemicalMainElement(mainElement, chemical, product,
                            productName, folderMicrotingId, areaRule, sdkSite, cpp.Locations);
                        List<Microting.eForm.Dto.KeyValuePair> options =
                            new List<Microting.eForm.Dto.KeyValuePair>();
                        int j = 0;
                        foreach (var s in cpp.Locations.Split("|"))
                        {
                            Microting.eForm.Dto.KeyValuePair keyValuePair =
                                new Microting.eForm.Dto.KeyValuePair(j.ToString(), s, false, j.ToString());
                            options.Add(keyValuePair);
                            j++;
                        }

                        MultiSelect multiSelect = new MultiSelect(0, false, false,
                            "Vælg rum som kemiproduktet skal fjernes fra", " ", Constants.FieldColors.Yellow, -1,
                            false, options);
                        ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1]).DataItemList
                            .Add(multiSelect);

                        if (string.IsNullOrEmpty(chemical.Use))
                        {
                            ((DataElement) mainElement.ElementList[0]).DataItemGroupList
                                .RemoveAt(0);
                        }

                        var caseId = await sdkCore.CaseCreate(mainElement, "", (int) sdkSite.MicrotingUid,
                            folder.Id);
                        var thisDbCase = await sdkDbContext.CheckListSites.AsNoTracking()
                            .FirstAsync(x => x.MicrotingUid == caseId);

                        var propertySites = await backendConfigurationPnDbContext.PropertyWorkers
                            .Where(x => x.PropertyId == areaRule.PropertyId).ToListAsync();

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
                                    sdkDbContext.Sites.FirstOrDefaultAsync(x => x.Id == propertyWorker.WorkerId);
                                var siteCaseId = await sdkCore.CaseCreate(mainElement, "", (int) site!.MicrotingUid!,
                                    folder.Id);
                                var chemicalProductPropertySite = new ChemicalProductPropertySite
                                {
                                    ChemicalId = chemical.Id,
                                    SdkCaseId = (int) siteCaseId!,
                                    SdkSiteId = site!.Id,
                                    PropertyId = areaRule.PropertyId,
                                    LanguageId = language.Id
                                };
                                await chemicalProductPropertySite.Create(backendConfigurationPnDbContext);
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
                        await newPlanning.Create(itemsPlanningPnDbContext);
                        var newPlanningCase = new PlanningCase
                        {
                            PlanningId = newPlanning.Id,
                            Status = 66,
                            MicrotingSdkeFormId = checkListTranslation.CheckListId
                        };
                        await newPlanningCase.Create(itemsPlanningPnDbContext);
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

                        await newPlanningCaseSite.Create(itemsPlanningPnDbContext);

                        var newAreaRulePlanning = CreateAreaRulePlanningObject(areaRulePlanningModel, areaRule,
                            newPlanning.Id,
                            areaRule.FolderId);

                        await newAreaRulePlanning.Create(backendConfigurationPnDbContext);
                        ChemicalProductProperty chemicalProductProperty = new ChemicalProductProperty
                        {
                            ChemicalId = chemical.Id,
                            PropertyId = areaRule.PropertyId,
                            SdkCaseId = (int) caseId,
                            Locations = cpp.Locations,
                            LanguageId = language.Id,
                            SdkSiteId = (int) sdkSite.MicrotingUid
                        };

                        await chemicalProductProperty.Create(backendConfigurationPnDbContext);
                    }
                }
            }
        }
    }

    private async Task<MainElement> ModifyChemicalMainElement(MainElement mainElement, Chemical chemical,
        Product product, string productName, string folderMicrotingId, AreaRule areaRule, Site sdkSite, string locations)
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
            $"<br><strong>Placering</strong><br>Ejendom: {areaRule.Property.Name}<br>Rum: {locations}<br><br><strong>Udløbsdato: </strong><br>";

        if (chemical.UseAndPossesionDeadline != null)
        {
            mainElement.ElementList.First().Description.InderValue += $"Dato: {chemical.UseAndPossesionDeadline:dd-MM-yyyy}<br><br>";
        }
        else
        {
            mainElement.ElementList.First().Description.InderValue += $"Dato: {chemical.AuthorisationExpirationDate:dd-MM-yyyy}<br><br>";
        }
        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Label = productName;
        ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
            .InderValue =
            $"{chemical.AuthorisationHolder.Name}<br>" +
            $"Reg nr.: {chemical.RegistrationNo}<br><br>";

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
            "<u>Produktstatus</u><br>" +
            $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.ProductStatusType.FirstOrDefault(x => x.Key == chemical.Status).Value}<br><br>";

        if (chemical.PestControlType == 2)
        {
            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue += $"<u>Pesticid produktgruppe</u><br>";
            foreach (var i in chemical.PesticideProductGroup)
            {
                ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                    .InderValue +=
                    $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.ProductGroupPesticide.FirstOrDefault(x => x.Key == i).Value}<br>";
            }
        }
        else
        {
            ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                .InderValue += $"<u>Biocid produktgruppe</u><br>";
            if (chemical.BiocideProductGroup != null)
            {
                ((None) ((DataElement) mainElement.ElementList[0]).DataItemList[0]).Description
                    .InderValue +=
                    $"{Microting.EformBackendConfigurationBase.Infrastructure.Const.Constants.ProductGroupBiocide.FirstOrDefault(x => x.Key == chemical.BiocideProductGroup).Value}<br>";
            }
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
        ((CheckBox) ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1]).DataItemList[0]).Label =
            "Produkt fjernet";
        ((SaveButton) ((FieldContainer) ((DataElement) mainElement.ElementList[0]).DataItemGroupList[1]).DataItemList[1]).Label =
            "Bekræft produkt fjernet";
        if (product != null)
        {
            if (!string.IsNullOrEmpty(product.FileName))
            {
                using var webClient = new HttpClient();
                Console.WriteLine(
                    $"info: Trying to download https://chemicalbase.microting.com/api/chemicals-pn/get-pdf-file?fileName={product.FileName}");
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

                var pdfId = await sdkCore.PdfUpload(Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"));

                await sdkCore.PutFileToStorageSystem(Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"),
                    $"{product.FileName}.pdf");
                File.Delete(Path.Combine(Path.GetTempPath(), $"{product.FileName}.pdf"));

                ((ShowPdf)((DataElement)mainElement.ElementList[0]).DataItemList[1]).Value = pdfId;
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
        var _backendConfigurationPnDbContext = backendConfigurationDbContextHelper.GetDbContext();
        var propertyItemPlanningTagId = await _backendConfigurationPnDbContext.Properties
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
            areaRulePlanning.Type = (AreaRuleT2TypesEnum)areaRule.Type;
        }

        if (areaRule.Alarm != null)
        {
            areaRulePlanning.Alarm = (AreaRuleT2AlarmsEnum)areaRule.Alarm;
        }

        return areaRulePlanning;
    }
}