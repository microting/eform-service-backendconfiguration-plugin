using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Dto;
using Microting.eForm.Helpers;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Models;
using Microting.eFormApi.BasePn.Infrastructure.Consts;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
// using QuestPDF.Fluent;
using Rebus.Handlers;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Messages;
using ServiceBackendConfigurationPlugin.Resources;
using File = System.IO.File;
using KeyValuePair = Microting.eForm.Dto.KeyValuePair;
// using Unit = QuestPDF.Infrastructure.Unit;

namespace ServiceBackendConfigurationPlugin.Handlers;

public class WorkOrderCaseCompletedHandler(
    BackendConfigurationDbContextHelper backendConfigurationDbContextHelper,
    ItemsPlanningDbContextHelper itemsPlanningDbContextHelper,
    eFormCore.Core sdkCore)
    : IHandleMessages<WorkOrderCaseCompleted>
{
    public async Task Handle(WorkOrderCaseCompleted message)
    {
        Console.WriteLine("WorkOrderCaseCompletedHandler .Handle called");
        Console.WriteLine($"message.CaseId: {message.CaseId}");
        Console.WriteLine($"message.MicrotingUId: {message.MicrotingUId}");
        Console.WriteLine($"message.CheckId: {message.CheckId}");
        Console.WriteLine($"message.SiteUId: {message.SiteUId}");
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        await using var
            itemsPlanningPnDbContext = itemsPlanningDbContextHelper.GetDbContext();
        await using var backendConfigurationPnDbContext =
            backendConfigurationDbContextHelper.GetDbContext();

        var eformQuery = sdkDbContext.CheckListTranslations
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .AsQueryable();

        var eformIdForNewTasks = await sdkDbContext.CheckLists
            .Where(x => x.OriginalId == "142663new2")
            .Where(x => x.ParentId == null)
            .Select(x => x.Id)
            .FirstAsync();

        var subCheckListNewTask = await sdkDbContext.CheckLists
            .FirstOrDefaultAsync(x => x.ParentId == eformIdForNewTasks)
            .ConfigureAwait(false);

        var eformIdForOngoingTasks = await sdkDbContext.CheckLists
            .Where(x => x.OriginalId == "142664new2")
            .Where(x => x.ParentId == null)
            .Select(x => x.Id)
            .FirstOrDefaultAsync();

        var subCheckList = await sdkDbContext.CheckLists
            .FirstOrDefaultAsync(x => x.ParentId == eformIdForOngoingTasks)
            .ConfigureAwait(false);

        var dbCase = await sdkDbContext.Cases
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Id == message.CaseId) ??
                     await sdkDbContext.Cases
                         .FirstOrDefaultAsync(x => x.MicrotingCheckUid == message.CheckId);

        var workOrderCase = await backendConfigurationPnDbContext.WorkorderCases
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.CaseId == message.MicrotingUId)
            .Include(x => x.ParentWorkorderCase)
            .Include(x => x.PropertyWorker)
            .ThenInclude(x => x.Property)
            .ThenInclude(x => x.PropertyWorkers)
            .ThenInclude(x => x.WorkorderCases)
            .FirstOrDefaultAsync();

        if (eformIdForNewTasks == dbCase.CheckListId && workOrderCase != null)
        {
            Console.WriteLine($"It's a new task");
            var property = workOrderCase.PropertyWorker.Property;

            var propertyWorkers = property.PropertyWorkers
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.TaskManagementEnabled == true || x.TaskManagementEnabled == null)
                .ToList();

            var createdBySite = await sdkDbContext.Sites.FirstAsync(x => x.Id == dbCase.SiteId);
            var language = await sdkDbContext.Languages.FirstOrDefaultAsync(x => x.Id == createdBySite.LanguageId) ??
                           await sdkDbContext.Languages.FirstOrDefaultAsync(x =>
                               x.LanguageCode == LocaleNames.Danish);

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language.LanguageCode);

            var priorityFiled =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckListNewTask.Id && x.OriginalId == "376935");
            var priorityFieldValue =
                await sdkDbContext.FieldValues.FirstOrDefaultAsync(x =>
                    x.FieldId == priorityFiled.Id && x.CaseId == dbCase.Id);

            var areaField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckListNewTask.Id && x.OriginalId == "375723");
            var areaFieldValue =
                await sdkDbContext.FieldValues.FirstOrDefaultAsync(x =>
                    x.FieldId == areaField.Id && x.CaseId == dbCase.Id);

            var pictureField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckListNewTask.Id && x.OriginalId == "375724");
            var pictureFieldValues = await sdkDbContext.FieldValues
                .Where(x => x.FieldId == pictureField.Id && x.CaseId == dbCase.Id).ToListAsync();

            var commentField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckListNewTask.Id && x.OriginalId == "375725");
            var commentFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(
                    x => x.FieldId == commentField.Id && x.CaseId == dbCase.Id);

            var createdByTexField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckListNewTask.Id && x.OriginalId == "375726");
            var createdByTextFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == createdByTexField.Id && x.CaseId == dbCase.Id);

            var assignToSelectField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckListNewTask.Id && x.OriginalId == "375727");
            var assignedToSelectFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == assignToSelectField.Id && x.CaseId == dbCase.Id);

            var updatedByName = createdBySite.Name;

            var areasGroup = await sdkDbContext.EntityGroups
                .FirstAsync(x => x.Id == property.EntitySelectListAreas);
            var deviceUsersGroup = await sdkDbContext.EntityGroups
                .FirstAsync(x => x.Id == property.EntitySelectListDeviceUsers);

            var assignedToEntityItem = !string.IsNullOrEmpty(assignedToSelectFieldValue.Value) ? await sdkDbContext.EntityItems.FirstOrDefaultAsync(x =>
                    x.EntityGroupId == deviceUsersGroup.Id && x.Id == int.Parse(assignedToSelectFieldValue.Value)) ??
                await sdkDbContext.EntityItems.FirstOrDefaultAsync(x =>
                    x.EntityGroupId == deviceUsersGroup.Id) : await sdkDbContext.EntityItems
                    .FirstOrDefaultAsync(x =>
                        x.EntityGroupId == deviceUsersGroup.Id);

            var propertyWorker = await backendConfigurationPnDbContext.PropertyWorkers.Where(x => x.EntityItemId == assignedToEntityItem.Id).FirstOrDefaultAsync();

            var assignedSite = await sdkDbContext.Sites.FirstAsync(x => x.Id == propertyWorker.WorkerId);
            var assignedToName = assignedToEntityItem?.Name ?? "";
            var areaName = "";
            if (areaFieldValue != null)
            {
                if (!string.IsNullOrEmpty(areaFieldValue!.Value) && areaFieldValue!.Value != "null")
                {
                    var area = await sdkDbContext.EntityItems.FirstAsync(x =>
                        x.EntityGroupId == areasGroup.Id && x.Id == int.Parse(areaFieldValue.Value));
                    areaName = area.Name;
                }
            }

            if (backendConfigurationPnDbContext.WorkorderCases.Any(x =>
                    x.ParentWorkorderCaseId == workOrderCase.Id
                    && x.WorkflowState != Constants.WorkflowStates.Removed
                    && x.CaseId == dbCase.MicrotingUid
                    && x.PropertyWorkerId == workOrderCase.PropertyWorkerId
                    && x.SelectedAreaName == areaName
                    && x.CreatedByName == createdBySite.Name
                    && x.CreatedByText == createdByTextFieldValue.Value
                    && x.CaseStatusesEnum == CaseStatusesEnum.Ongoing
                    && x.Description == commentFieldValue.Value))
            {
                return;
            }

            var newWorkOrderCase = new WorkorderCase
            {
                ParentWorkorderCaseId = workOrderCase.Id,
                CaseId = 0,
                PropertyWorkerId = workOrderCase.PropertyWorkerId,
                SelectedAreaName = areaName,
                CreatedByName = createdBySite.Name,
                LastUpdatedByName = createdBySite.Name,
                CreatedBySdkSiteId = createdBySite.Id,
                UpdatedBySdkSiteId = createdBySite.Id,
                AssignedToSdkSiteId = assignedSite.Id,
                LastAssignedToName = assignedSite.Name,
                CreatedByText = createdByTextFieldValue.Value,
                CaseStatusesEnum = CaseStatusesEnum.Ongoing,
                Description = commentFieldValue.Value,
                CaseInitiated = DateTime.UtcNow,
                LeadingCase = false,
                Priority = priorityFieldValue != null ? priorityFieldValue.Value : "4"
            };
            await newWorkOrderCase.Create(backendConfigurationPnDbContext);

            var picturesOfTasks = new List<string>();
            var picturesOfTasksList = new List<KeyValuePair<string, string>>();
            bool hasImages = false;
            foreach (var pictureFieldValue in pictureFieldValues.Where(pictureFieldValue =>
                         pictureFieldValue.UploadedDataId != null))
            {
                var uploadedData =
                    await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == pictureFieldValue.UploadedDataId);
                var workOrderCaseImage = new WorkorderCaseImage
                {
                    WorkorderCaseId = newWorkOrderCase.Id,
                    UploadedDataId = (int)pictureFieldValue.UploadedDataId!
                };

                picturesOfTasks.Add($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}");
                picturesOfTasksList.Add(new KeyValuePair<string, string>($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}", uploadedData.Checksum));
                await workOrderCaseImage.Create(backendConfigurationPnDbContext);
                hasImages = true;
            }

            // var hash = await GeneratePdf(picturesOfTasks, assignedSite.Id!);

            var priorityText = "";

            switch (workOrderCase.Priority)
            {
                case "1":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Urgent}<br><br>";
                    break;
                case "2":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.High}<br><br>";
                    break;
                case "3":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Medium}<br><br>";
                    break;
                case "4":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Low}<br><br>";
                    break;
            }

            var label = $"<strong>{SharedResource.AssignedTo}:</strong> {assignedToName}<br>" +
                        $"<strong>{SharedResource.Location}:</strong> {property.Name}<br>" +
                        (!string.IsNullOrEmpty(areaName)
                            ? $"<strong>{SharedResource.Area}:</strong> {areaName}<br>"
                            : "") +
                        $"<strong>{SharedResource.Description}:</strong> {commentFieldValue.Value}<br>" +
                        priorityText +
                        $"<strong>{SharedResource.CreatedBy}:</strong> {createdBySite.Name}<br>" +
                        (!string.IsNullOrEmpty(createdByTextFieldValue.Value)
                            ? $"<strong>{SharedResource.CreatedBy}:</strong> {createdByTextFieldValue.Value}<br>"
                            : "") +
                        $"<strong>{SharedResource.CreatedDate}:</strong> {newWorkOrderCase.CaseInitiated: dd.MM.yyyy}<br><br>" +
                        $"<strong>{SharedResource.Status}:</strong> {SharedResource.Ongoing}<br><br>";

            var pushMessageTitle = !string.IsNullOrEmpty(areaName)
                ? $"{property.Name}; {areaName}"
                : $"{property.Name}";
            var pushMessageBody = $"{commentFieldValue.Value}";


            // deploy eform to ongoing status
            await DeployWorkOrderEform(propertyWorkers, eformIdForOngoingTasks,
                property, label, CaseStatusesEnum.Ongoing, newWorkOrderCase,
                commentFieldValue.Value, int.Parse(deviceUsersGroup.MicrotingUid),
                assignedSite, pushMessageBody, pushMessageTitle, updatedByName, hasImages, picturesOfTasksList);
        }
        else if (eformIdForOngoingTasks == dbCase.CheckListId && workOrderCase != null)
        {
            Console.WriteLine($"It's an ongoing task");
            var property = workOrderCase.PropertyWorker.Property;

            var propertyWorkers = property.PropertyWorkers
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.TaskManagementEnabled == true || x.TaskManagementEnabled == null)
                .ToList();

            var updatedBySite = await sdkDbContext.Sites.FirstAsync(x => x.Id == dbCase.SiteId);
            var language = await sdkDbContext.Languages.FirstOrDefaultAsync(x => x.Id == updatedBySite.LanguageId) ??
                           await sdkDbContext.Languages.FirstAsync(x => x.LanguageCode == LocaleNames.Danish);

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language.LanguageCode);

            var deviceUsersGroup = await sdkDbContext.EntityGroups
                .FirstAsync(x => x.Id == property.EntitySelectListDeviceUsers);

            var pictureField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckList.Id && x.OriginalId == "375731");
            var pictureFieldValues = await sdkDbContext.FieldValues
                .Where(x => x.FieldId == pictureField.Id && x.CaseId == dbCase.Id).ToListAsync();

            var commentField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckList.Id && x.OriginalId == "375732");
            var commentFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x => x.FieldId == commentField.Id && x.CaseId == dbCase.Id);

            var priorityFiled =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckList.Id && x.OriginalId == "376935");
            var priorityFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == priorityFiled.Id && x.CaseId == dbCase.Id);

            var assignToSelectField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckList.Id && x.OriginalId == "375733");
            var assignedToSelectFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == assignToSelectField.Id && x.CaseId == dbCase.Id);

            var statusField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == subCheckList.Id && x.OriginalId == "375734");
            var statusFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x => x.FieldId == statusField.Id && x.CaseId == dbCase.Id);

            var assignedToEntityItem = !string.IsNullOrEmpty(assignedToSelectFieldValue.Value) ? await sdkDbContext.EntityItems.FirstOrDefaultAsync(x =>
                    x.EntityGroupId == deviceUsersGroup.Id && x.Id == int.Parse(assignedToSelectFieldValue.Value)) ??
                await sdkDbContext.EntityItems.FirstOrDefaultAsync(x =>
                    x.EntityGroupId == deviceUsersGroup.Id) : await sdkDbContext.EntityItems
                    .FirstOrDefaultAsync(x =>
                        x.EntityGroupId == deviceUsersGroup.Id);
            var propertyWorker = await backendConfigurationPnDbContext.PropertyWorkers.Where(x => x.EntityItemId == assignedToEntityItem.Id).FirstOrDefaultAsync();

            var assignedSite = await sdkDbContext.Sites.FirstAsync(x => x.Id == propertyWorker.WorkerId);
            var createdBySite =
                await sdkDbContext.Sites.FirstOrDefaultAsync(x => x.Id == workOrderCase.CreatedBySdkSiteId);
            workOrderCase.CreatedByName ??= createdBySite.Name;
            //var assignedToName = assignedToEntityItem?.Name ?? "";
            var textStatus = "";

            workOrderCase.Priority = priorityFieldValue.Value;
            workOrderCase.UpdatedBySdkSiteId = updatedBySite.Id;
            workOrderCase.LastUpdatedByName = updatedBySite.Name;

            switch (statusFieldValue.Value)
            {
                case "1":
                    textStatus = SharedResource.Ongoing;
                    workOrderCase.CaseStatusesEnum = CaseStatusesEnum.Ongoing;
                    break;
                case "2":
                    textStatus = SharedResource.Completed;
                    workOrderCase.CaseStatusesEnum = CaseStatusesEnum.Completed;
                    break;
                case "3":
                    textStatus = SharedResource.Ordered;
                    workOrderCase.CaseStatusesEnum = CaseStatusesEnum.Ordered;
                    break;
                case "4":
                    textStatus = SharedResource.Awaiting;
                    workOrderCase.CaseStatusesEnum = CaseStatusesEnum.Awaiting;
                    break;
                default:
                    textStatus = SharedResource.Ongoing;
                    workOrderCase.CaseStatusesEnum = CaseStatusesEnum.Ongoing;
                    break;
            }

            var updatedByName = updatedBySite.Name;

            var picturesOfTasks = new List<string>();
            var picturesOfTasksList = new List<KeyValuePair<string, string>>();
            bool hasImages = false;
            foreach (var pictureFieldValue in pictureFieldValues)
            {
                if (pictureFieldValue.UploadedDataId != null)
                {
                    var uploadedData =
                        await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == pictureFieldValue.UploadedDataId);
                    var workOrderCaseImage = new WorkorderCaseImage
                    {
                        WorkorderCaseId = workOrderCase.Id,
                        UploadedDataId = (int)pictureFieldValue.UploadedDataId!
                    };

                    picturesOfTasks.Add($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}");
                    picturesOfTasksList.Add(new KeyValuePair<string, string>($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}", uploadedData.Checksum));
                    await workOrderCaseImage.Create(backendConfigurationPnDbContext);
                    hasImages = true;
                }
            }

            var parentCaseImages = await backendConfigurationPnDbContext.WorkorderCaseImages
                .Where(x => x.WorkorderCaseId == workOrderCase.ParentWorkorderCaseId).ToListAsync();

            foreach (var workorderCaseImage in parentCaseImages)
            {
                var uploadedData =
                    await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == workorderCaseImage.UploadedDataId);
                picturesOfTasks.Add($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}");
                picturesOfTasksList.Add(new KeyValuePair<string, string>($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}", uploadedData.Checksum));
                var workOrderCaseImage = new WorkorderCaseImage
                {
                    WorkorderCaseId = workOrderCase.Id,
                    UploadedDataId = uploadedData.Id
                };
                await workOrderCaseImage.Create(backendConfigurationPnDbContext);
                hasImages = true;
            }

            // var hash = await GeneratePdf(picturesOfTasks, updatedBySite.Id);

            var description = $"<strong>{SharedResource.AssignedTo}:</strong> {assignedSite.Name}<br>";

            var pushMessageTitle = !string.IsNullOrEmpty(workOrderCase.SelectedAreaName)
                ? $"{property.Name}; {workOrderCase.SelectedAreaName}"
                : $"{property.Name}";
            var pushMessageBody = $"{commentFieldValue.Value}";
            var deviceUsersGroupUid = await sdkDbContext.EntityGroups
                .Where(x => x.Id == property.EntitySelectListDeviceUsers)
                .Select(x => x.MicrotingUid)
                .FirstAsync();

            var priorityText = "";

            switch (workOrderCase.Priority)
            {
                case "1":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Urgent}<br><br>";
                    break;
                case "2":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.High}<br><br>";
                    break;
                case "3":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Medium}<br><br>";
                    break;
                case "4":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Low}<br><br>";
                    break;
            }

            description += $"<strong>{SharedResource.Location}:</strong> {property.Name}<br>" +
                           (!string.IsNullOrEmpty(workOrderCase.SelectedAreaName)
                               ? $"<strong>{SharedResource.Area}:</strong> {workOrderCase.SelectedAreaName}<br>"
                               : "") +
                           $"<strong>{SharedResource.Description}:</strong> {commentFieldValue.Value}<br>" +
                           priorityText +
                           $"<strong>{SharedResource.CreatedBy}:</strong> {workOrderCase.CreatedByName}<br>" +
                           (!string.IsNullOrEmpty(workOrderCase.CreatedByText)
                               ? $"<strong>{SharedResource.CreatedBy}:</strong> {workOrderCase.CreatedByText}<br>"
                               : "") +
                           $"<strong>{SharedResource.CreatedDate}:</strong> {workOrderCase.CaseInitiated: dd.MM.yyyy}<br><br>" +
                           $"<strong>{SharedResource.LastUpdatedBy}:</strong> {updatedBySite.Name}<br>" +
                           $"<strong>{SharedResource.LastUpdatedDate}:</strong> {DateTime.UtcNow: dd.MM.yyyy}<br><br>" +
                           $"<strong>{SharedResource.Status}:</strong> {textStatus}<br><br>";
            // retract eform
            await RetractEform(workOrderCase);
            // deploy eform to ongoing status
            await DeployWorkOrderEform(propertyWorkers, eformIdForOngoingTasks, property, description,
                workOrderCase.CaseStatusesEnum, workOrderCase, commentFieldValue.Value, int.Parse(deviceUsersGroupUid), assignedSite, pushMessageBody, pushMessageTitle, updatedByName, hasImages, picturesOfTasksList);
        }
    }

    private async Task DeployWorkOrderEform(
        List<PropertyWorker> propertyWorkers,
        int eformId,
        Property property,
        string description,
        CaseStatusesEnum status,
        WorkorderCase workorderCase,
        string newDescription,
        int? deviceUsersGroupId,
        // string pdfHash,
        Site assignedSite,
        string pushMessageBody,
        string pushMessageTitle,
        string updatedByName,
        bool hasImages,
        List<KeyValuePair<string, string>> picturesOfTasks)
    {
        Console.WriteLine($"Deploying eform to {propertyWorkers.Count} workers");
        int? folderId = null;
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        await using var backendConfigurationPnDbContext =
            backendConfigurationDbContextHelper.GetDbContext();
        var i = 0;
        DateTime startDate = new DateTime(2022, 12, 5);
        var displayOrder = (int)(DateTime.UtcNow - startDate).TotalSeconds;
        foreach (var propertyWorker in propertyWorkers)
        {
            Console.WriteLine($"Deploying to worker {propertyWorker.WorkerId}");
            var priorityText = "";

            var site = await sdkDbContext.Sites.FirstAsync(x => x.Id == propertyWorker.WorkerId);
            var unit = await sdkDbContext.Units.FirstAsync(x => x.SiteId == site.Id);
            var siteLanguage = await sdkDbContext.Languages.SingleAsync(x => x.Id == site.LanguageId).ConfigureAwait(false);
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(siteLanguage.LanguageCode);
            switch (workorderCase.Priority)
            {
                case "1":
                    displayOrder = 100_000_000 + displayOrder;
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Urgent}<br>";
                    break;
                case "2":
                    displayOrder = 200_000_000 + displayOrder;
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.High}<br>";
                    break;
                case "3":
                    displayOrder = 300_000_000 + displayOrder;
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Medium}<br>";
                    break;
                case "4":
                    displayOrder = 400_000_000 + displayOrder;
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Low}<br>";
                    break;
            }

            var textStatus = "";

            switch (workorderCase.CaseStatusesEnum)
            {
                case CaseStatusesEnum.Ongoing:
                    textStatus = SharedResource.Ongoing;
                    break;
                case CaseStatusesEnum.Completed:
                    textStatus = SharedResource.Completed;
                    break;
                case CaseStatusesEnum.Awaiting:
                    textStatus = SharedResource.Awaiting;
                    break;
                case CaseStatusesEnum.Ordered:
                    textStatus = SharedResource.Ordered;
                    break;
            }

            var assignedTo = "";
            var mainElement = await sdkCore.ReadeForm(eformId, siteLanguage);
            mainElement.Label = " ";
            mainElement.ElementList[0].QuickSyncEnabled = true;
            mainElement.EnableQuickSync = true;
            mainElement.ElementList[0].Label = " ";

            mainElement.DisplayOrder = displayOrder; // Lowest value is the top of the list

            // if (status == CaseStatusesEnum.Completed || site.Name == siteName)
            // {
            // }
            //assignedTo = site.Name == assignedSite.Name ? "" : $"<strong>{SharedResource.AssignedTo}:</strong> {assignedSite.Name}<br>";
            if (site.Name == assignedSite.Name)
            {
                mainElement.CheckListFolderName = sdkDbContext.Folders.First(x => x.Id == (workorderCase.Priority != "1" ? property.FolderIdForOngoingTasks : property.FolderIdForTasks))
                    .MicrotingUid.ToString();
                folderId = property.FolderIdForOngoingTasks;
                mainElement.PushMessageTitle = pushMessageTitle;
                mainElement.PushMessageBody = pushMessageBody;
                mainElement.BadgeCountEnabled = true;
            }
            else
            {
                folderId = property.FolderIdForCompletedTasks;
                mainElement.CheckListFolderName = sdkDbContext.Folders.First(x => x.Id == property.FolderIdForCompletedTasks)
                    .MicrotingUid.ToString();
            }

            //var assignedTo = site.Name == assignedSite.Name ? "" : $"<strong>{SharedResource.AssignedTo}:</strong> {assignedSite.Name}<br>";

            // var areaName = !string.IsNullOrEmpty(workorderCase.SelectedAreaName)
            //     ? $"<strong>{SharedResource.Area}:</strong> {workorderCase.SelectedAreaName}<br>"
            //     : "";

            var outerDescription = $"<strong>{SharedResource.AssignedTo}:</strong> {assignedSite.Name}<br>";
            outerDescription += $"<strong>{SharedResource.Location}:</strong> {property.Name}<br>" +
                                (!string.IsNullOrEmpty(workorderCase.SelectedAreaName)
                                    ? $"<strong>{SharedResource.Area}:</strong> {workorderCase.SelectedAreaName}<br>"
                                    : "") +
                                $"<strong>{SharedResource.Description}:</strong> {workorderCase.Description}<br>" +
                                priorityText +
                                $"<strong>{SharedResource.CreatedBy}:</strong> {workorderCase.CreatedByName}<br>" +
                                (!string.IsNullOrEmpty(workorderCase.CreatedByText)
                                    ? $"<strong>{SharedResource.CreatedBy}:</strong> {workorderCase.CreatedByText}<br>"
                                    : "") +
                                $"<strong>{SharedResource.CreatedDate}:</strong> {workorderCase.CaseInitiated: dd.MM.yyyy}<br><br>" +
                                $"<strong>{SharedResource.LastUpdatedBy}:</strong> {workorderCase.CreatedByName}<br>" +
                                $"<strong>{SharedResource.LastUpdatedDate}:</strong> {workorderCase.UpdatedAt: dd.MM.yyyy}<br><br>" +
                                $"<strong>{SharedResource.Status}:</strong> {textStatus}<br><br>";

            // var outerDescription = $"<strong>{SharedResource.Location}:</strong> {property.Name}<br>" +
            //                        areaName +
            //                        $"<strong>{SharedResource.Description}:</strong> {newDescription}<br>" +
            //                        priorityText +
            //                        assignedTo +
            //                        $"<strong>{SharedResource.Status}:</strong> {textStatus}<br><br>";
            mainElement.ElementList[0].Description.InderValue = outerDescription.Replace("\n", "<br>");
            // TODO uncomment when new app has been released.
            ((DataElement)mainElement.ElementList[0]).DataItemList[0].Description.InderValue = description.Replace("\n", "<br>");
            ((DataElement)mainElement.ElementList[0]).DataItemList[0].Label = " ";
            ((DataElement)mainElement.ElementList[0]).DataItemList[0].Color = Constants.FieldColors.Yellow;
            // ((ShowPdf) ((DataElement) mainElement.ElementList[0]).DataItemList[1]).Value = pdfHash;

            List<KeyValuePair> kvpList = ((SingleSelect) ((DataElement) mainElement.ElementList[0]).DataItemList[4]).KeyValuePairList;
            var newKvpList = new List<KeyValuePair>();
            foreach (var keyValuePair in kvpList)
            {
                if (keyValuePair.Key == workorderCase.Priority)
                {
                    keyValuePair.Selected = true;
                }
                newKvpList.Add(keyValuePair);
            }
            ((SingleSelect) ((DataElement) mainElement.ElementList[0]).DataItemList[4]).KeyValuePairList = newKvpList;

            if (deviceUsersGroupId != null)
            {
                ((EntitySelect)((DataElement)mainElement.ElementList[0]).DataItemList[5]).Source = (int)deviceUsersGroupId;
                ((EntitySelect)((DataElement)mainElement.ElementList[0]).DataItemList[5]).Mandatory = true;
                ((Comment)((DataElement)mainElement.ElementList[0]).DataItemList[3]).Value = newDescription;
                ((SingleSelect)((DataElement)mainElement.ElementList[0]).DataItemList[6]).Mandatory = true;
                mainElement.EndDate = DateTime.Now.AddYears(10).ToUniversalTime();
                mainElement.Repeated = 1;
            }
            else
            {
                mainElement.EndDate = DateTime.Now.AddDays(30).ToUniversalTime();
                mainElement.ElementList[0].DoneButtonEnabled = false;
                mainElement.Repeated = 1;
            }

            mainElement.StartDate = DateTime.Now.ToUniversalTime();
            // if (hasImages == false)
            // {
            ((DataElement) mainElement.ElementList[0]).DataItemList.RemoveAt(1);
            // }
            // unit.eFormVersion ??= "1.0.0";
            // if (int.Parse(unit.eFormVersion.Replace(".","")) > 3212)
            // {
            if (hasImages)
            {
                // ((DataElement) mainElement.ElementList[0]).DataItemList.RemoveAt(1);
                // add a new show picture element for each picture in the picturesOfTasks list
                int j = 0;
                foreach (var picture in picturesOfTasks)
                {
                    var showPicture = new ShowPicture(j, false, false, "", "", "", 0, false, "");
                    var storageResult = sdkCore.GetFileFromS3Storage(picture.Key).GetAwaiter().GetResult();

                    await sdkCore.PngUpload(storageResult.ResponseStream, picture.Value, picture.Key);
                    showPicture.Value = picture.Value;
                    ((DataElement) mainElement.ElementList[0]).DataItemList.Add(showPicture);
                    j++;
                }
            }
            // }
            // var caseId = await _sdkCore.CaseCreate(mainElement, "", (int)site.MicrotingUid, folderId);
            int caseId = 0;
            if (workorderCase.CaseStatusesEnum != CaseStatusesEnum.Completed)
            {
                caseId = (int)await sdkCore.CaseCreate(mainElement, "", (int)site.MicrotingUid, folderId);
            }
            await new WorkorderCase
            {
                CaseId = caseId,
                PropertyWorkerId = propertyWorker.Id,
                CaseStatusesEnum = status,
                ParentWorkorderCaseId = workorderCase.Id,
                SelectedAreaName = workorderCase.SelectedAreaName,
                CreatedByName = workorderCase.CreatedByName,
                CreatedBySdkSiteId = workorderCase.CreatedBySdkSiteId,
                UpdatedBySdkSiteId = workorderCase.UpdatedBySdkSiteId,
                CreatedByText = workorderCase.CreatedByText,
                Description = newDescription,
                CaseInitiated = workorderCase.CaseInitiated,
                LastAssignedToName = assignedSite.Name,
                AssignedToSdkSiteId = assignedSite.Id,
                LastUpdatedByName = updatedByName,
                LeadingCase = i == 0,
                Priority = workorderCase.Priority
            }.Create(backendConfigurationPnDbContext);
            i++;
        }
    }

    private async Task RetractEform(WorkorderCase workOrderCase)
    {
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        await using var backendConfigurationPnDbContext =
            backendConfigurationDbContextHelper.GetDbContext();

        if (workOrderCase.ParentWorkorderCaseId != null)
        {
            var workOrdersToRetract = await backendConfigurationPnDbContext.WorkorderCases
                .Where(x => x.ParentWorkorderCaseId == workOrderCase.ParentWorkorderCaseId).ToListAsync();

            foreach (var theCase in workOrdersToRetract)
            {
                try {
                    await sdkCore.CaseDelete(theCase.CaseId);
                } catch (Exception e) {
                    Console.WriteLine(e);
                    Console.WriteLine($"faild to delete case {theCase.CaseId}");
                }
                await theCase.Delete(backendConfigurationPnDbContext);
            }

            var parentCase = await backendConfigurationPnDbContext.WorkorderCases
                .FirstAsync(x => x.Id == workOrderCase.ParentWorkorderCaseId);

            if (parentCase.CaseId != 0 && parentCase.ParentWorkorderCaseId != null)
            {
                try
                {
                    await sdkCore.CaseDelete(parentCase.CaseId);
                } catch (Exception e) {
                    Console.WriteLine(e);
                    Console.WriteLine($"faild to delete case {parentCase.CaseId}");
                }
            }
            await parentCase.Delete(backendConfigurationPnDbContext);
        }
    }

    // private async Task<string> GeneratePdf(List<string> picturesOfTasks, int sitId)
    // {
    //     picturesOfTasks.Reverse();
    //     string downloadPath = Path.Combine(Path.GetTempPath(), "reports", "results");
    //     Directory.CreateDirectory(downloadPath);
    //     string timeStamp = DateTime.UtcNow.ToString("yyyyMMdd") + "_" + DateTime.UtcNow.ToString("hhmmss");
    //     string tempPdfFileName = $"{timeStamp}{sitId}_temp.pdf";
    //     string tempPdfFilePath = Path.Combine(downloadPath, tempPdfFileName);
    //     var document = Document.Create(container =>
    //     {
    //         container.Page(page =>
    //         {
    //             page.Content()
    //                 .Padding(1, Unit.Centimetre)
    //                 .Column(x =>
    //                 {
    //                     // loop over all images and add them to the document
    //                     var i = 0;
    //                     foreach (var imageName in picturesOfTasks)
    //                     {
    //                         var storageResult = sdkCore.GetFileFromS3Storage(imageName).GetAwaiter().GetResult();
    //                         x.Item().Image(storageResult.ResponseStream)
    //                             .FitArea();
    //                         if (i < picturesOfTasks.Count - 1)
    //                         {
    //                             x.Item().PageBreak();
    //                         }
    //                         i++;
    //                     }
    //                 });
    //         });
    //     }).GeneratePdf();
    //
    //     await using var fileStream = new FileStream(tempPdfFilePath, FileMode.Create, FileAccess.Write);
    //     // save the byte[] to a file.
    //     await fileStream.WriteAsync(document, 0, document.Length);
    //     await fileStream.FlushAsync();
    //
    //     // Upload PDF
    //     // string pdfFileName = null;
    //     string hash = await sdkCore.PdfUpload(tempPdfFilePath);
    //     if (hash != null)
    //     {
    //         //rename local file
    //         FileInfo fileInfo = new FileInfo(tempPdfFilePath);
    //         fileInfo.CopyTo(downloadPath + "/" + hash + ".pdf", true);
    //         fileInfo.Delete();
    //         await sdkCore.PutFileToStorageSystem(Path.Combine(downloadPath, $"{hash}.pdf"), $"{hash}.pdf");
    //
    //         // delete local file
    //         File.Delete(downloadPath + "/" + hash + ".pdf");
    //         // TODO Remove from file storage?
    //     }
    //
    //     return hash;
    // }
}