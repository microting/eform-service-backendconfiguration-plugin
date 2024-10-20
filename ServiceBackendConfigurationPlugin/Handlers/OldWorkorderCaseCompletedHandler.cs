using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Helpers;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Models;
using Microting.eFormApi.BasePn.Infrastructure.Consts;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Rebus.Handlers;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Messages;
using ServiceBackendConfigurationPlugin.Resources;
using File = System.IO.File;
using KeyValuePair = Microting.eForm.Dto.KeyValuePair;

namespace ServiceBackendConfigurationPlugin.Handlers;

public class OldWorkOrderCaseCompletedHandler(
    BackendConfigurationDbContextHelper backendConfigurationDbContextHelper,
    ItemsPlanningDbContextHelper itemsPlanningDbContextHelper,
    eFormCore.Core sdkCore)
    : IHandleMessages<OldWorkOrderCaseCompleted>
{
    public async Task Handle(OldWorkOrderCaseCompleted message)
    {
        Console.WriteLine("EFormCompletedHandler .Handle called");
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
            .Select(x => x.Id)
            .FirstAsync();

        var eformIdForNewTasksOld = await eformQuery
            .Where(x => x.Text == "01. New task")
            .Select(x => x.CheckListId)
            .FirstOrDefaultAsync();

        var eformIdForOngoingTasksOld = await eformQuery
            .Where(x => x.Text == "02. Ongoing task")
            .Select(x => x.CheckListId)
            .FirstOrDefaultAsync();

        var eformIdForOngoingTasks = await sdkDbContext.CheckLists
            .Where(x => x.OriginalId == "142664new2")
            .Select(x => x.Id)
            .FirstOrDefaultAsync();

        var dbCase = await sdkDbContext.Cases
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Id == message.CaseId) ??
                     await sdkDbContext.Cases
                         .FirstAsync(x => x.MicrotingCheckUid == message.CheckId);

        var workOrderCase = await backendConfigurationPnDbContext.WorkorderCases
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.CaseId == message.MicrotingUId)
            .Include(x => x.ParentWorkorderCase)
            .Include(x => x.PropertyWorker)
            .ThenInclude(x => x.Property)
            .ThenInclude(x => x.PropertyWorkers)
            .ThenInclude(x => x.WorkorderCases)
            .FirstOrDefaultAsync();

        if (eformIdForNewTasksOld == dbCase.CheckListId && workOrderCase != null)
        {
            var property = workOrderCase.PropertyWorker.Property;

            var propertyWorkers = property.PropertyWorkers
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.TaskManagementEnabled == true || x.TaskManagementEnabled == null)
                .ToList();

            var site = await sdkDbContext.Sites.FirstAsync(x => x.Id == dbCase.SiteId);
            var language = await sdkDbContext.Languages.FirstOrDefaultAsync(x => x.Id == site.LanguageId) ??
                           await sdkDbContext.Languages.FirstOrDefaultAsync(x =>
                               x.LanguageCode == LocaleNames.Danish);

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language.LanguageCode);

            var areaField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == eformIdForNewTasksOld + 1 && x.DisplayIndex == 2);
            var areaFieldValue =
                await sdkDbContext.FieldValues.FirstOrDefaultAsync(x =>
                    x.FieldId == areaField.Id && x.CaseId == dbCase.Id);

            var pictureField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == eformIdForNewTasksOld + 1 && x.DisplayIndex == 3);
            var pictureFieldValues = await sdkDbContext.FieldValues
                .Where(x => x.FieldId == pictureField.Id && x.CaseId == dbCase.Id).ToListAsync();

            var commentField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == eformIdForNewTasksOld + 1 && x.DisplayIndex == 4);
            var commentFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(
                    x => x.FieldId == commentField.Id && x.CaseId == dbCase.Id);

            var assignToTexField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == eformIdForNewTasksOld + 1 && x.DisplayIndex == 5);
            var assignedToFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == assignToTexField.Id && x.CaseId == dbCase.Id);

            var assignToSelectField =
                await sdkDbContext.Fields.FirstAsync(x =>
                    x.CheckListId == eformIdForNewTasksOld + 1 && x.DisplayIndex == 6);
            var assignedToSelectFieldValue =
                await sdkDbContext.FieldValues.FirstAsync(x =>
                    x.FieldId == assignToSelectField.Id && x.CaseId == dbCase.Id);

            var updatedByName = site.Name;

            var areasGroup = await sdkDbContext.EntityGroups
                .FirstAsync(x => x.Id == property.EntitySelectListAreas);
            var deviceUsersGroup = await sdkDbContext.EntityGroups
                .FirstAsync(x => x.Id == property.EntitySelectListDeviceUsers);

            var assignedTo = await sdkDbContext.EntityItems.FirstAsync(x =>
                x.EntityGroupId == deviceUsersGroup.Id && x.Id == int.Parse(assignedToSelectFieldValue.Value));
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
                    && x.CreatedByName == site.Name
                    && x.CreatedByText == assignedToFieldValue.Value
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
                CreatedByName = site.Name,
                CreatedByText = assignedToFieldValue.Value,
                CaseStatusesEnum = CaseStatusesEnum.Ongoing,
                Description = commentFieldValue.Value,
                CaseInitiated = DateTime.UtcNow,
                LeadingCase = false,
                Priority = "3"
            };
            await newWorkOrderCase.Create(backendConfigurationPnDbContext);

            var picturesOfTasks = new List<string>();
            foreach (var pictureFieldValue in pictureFieldValues.Where(pictureFieldValue =>
                         pictureFieldValue.UploadedDataId != null))
            {
                var uploadedData =
                    await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == pictureFieldValue.UploadedDataId);
                var workOrderCaseImage = new WorkorderCaseImage
                {
                    WorkorderCaseId = newWorkOrderCase.Id,
                    UploadedDataId = (int) pictureFieldValue.UploadedDataId!
                };

                picturesOfTasks.Add($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}");
                await workOrderCaseImage.Create(backendConfigurationPnDbContext);
            }

            var hash = await GeneratePdf(picturesOfTasks, (int) site.Id);

            var label = $"<strong>{SharedResource.AssignedTo}:</strong> {assignedTo.Name}<br>" +
                        $"<strong>{SharedResource.Location}:</strong> {property.Name}<br>" +
                        (!string.IsNullOrEmpty(areaName)
                            ? $"<strong>{SharedResource.Area}:</strong> {areaName}<br>"
                            : "") +
                        $"<strong>{SharedResource.Description}:</strong> {commentFieldValue.Value}<br><br>" +
                        $"<strong>{SharedResource.CreatedBy}:</strong> {site.Name}<br>" +
                        (!string.IsNullOrEmpty(assignedToFieldValue.Value)
                            ? $"<strong>{SharedResource.CreatedBy}:</strong> {assignedToFieldValue.Value}<br>"
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
                commentFieldValue.Value, int.Parse(deviceUsersGroup.MicrotingUid), hash,
                assignedTo.Name, pushMessageBody, pushMessageTitle, updatedByName);
        }else if (eformIdForOngoingTasksOld == dbCase.CheckListId && workOrderCase != null)
        {
            var property = workOrderCase.PropertyWorker.Property;

            var propertyWorkers = property.PropertyWorkers
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.TaskManagementEnabled == true || x.TaskManagementEnabled == null)
                .ToList();

            var site = await sdkDbContext.Sites.FirstAsync(x => x.Id == dbCase.SiteId);
            var language = await sdkDbContext.Languages.FirstOrDefaultAsync(x => x.Id == site.LanguageId) ??
                           await sdkDbContext.Languages.FirstAsync(x => x.LanguageCode == LocaleNames.Danish);

            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language.LanguageCode);

            var deviceUsersGroup = await sdkDbContext.EntityGroups
                .FirstAsync(x => x.Id == property.EntitySelectListDeviceUsers);

            var pictureField =
                await sdkDbContext.Fields.FirstAsync(x => x.CheckListId == eformIdForOngoingTasksOld + 1 && x.DisplayIndex == 2);
            var pictureFieldValues = await sdkDbContext.FieldValues.Where(x => x.FieldId == pictureField.Id && x.CaseId == dbCase.Id).ToListAsync();

            var commentField =
                await sdkDbContext.Fields.FirstAsync(x => x.CheckListId == eformIdForOngoingTasksOld + 1 && x.DisplayIndex == 3);
            var commentFieldValue = await sdkDbContext.FieldValues.FirstAsync(x => x.FieldId == commentField.Id && x.CaseId == dbCase.Id);

            var assignToSelectField =
                await sdkDbContext.Fields.FirstAsync(x => x.CheckListId == eformIdForOngoingTasksOld + 1 && x.DisplayIndex == 4);
            var assignedToSelectFieldValue = await sdkDbContext.FieldValues.FirstAsync(x => x.FieldId == assignToSelectField.Id && x.CaseId == dbCase.Id);

            var statusField =
                await sdkDbContext.Fields.FirstAsync(x => x.CheckListId == eformIdForOngoingTasksOld + 1 && x.DisplayIndex == 5);
            var statusFieldValue = await sdkDbContext.FieldValues.FirstAsync(x => x.FieldId == statusField.Id && x.CaseId == dbCase.Id);

            var assignedTo = await sdkDbContext.EntityItems.FirstAsync(x => x.EntityGroupId == deviceUsersGroup.Id && x.Id == int.Parse(assignedToSelectFieldValue.Value));
            var textStatus = "";

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
            }
            var updatedByName = site.Name;

            var picturesOfTasks = new List<string>();
            foreach (var pictureFieldValue in pictureFieldValues)
            {
                if (pictureFieldValue.UploadedDataId != null)
                {
                    var uploadedData = await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == pictureFieldValue.UploadedDataId);
                    var workOrderCaseImage = new WorkorderCaseImage
                    {
                        WorkorderCaseId = workOrderCase.Id,
                        UploadedDataId = (int) pictureFieldValue.UploadedDataId!
                    };

                    picturesOfTasks.Add($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}");
                    await workOrderCaseImage.Create(backendConfigurationPnDbContext);
                }
            }
            var parentCaseImages = await backendConfigurationPnDbContext.WorkorderCaseImages.Where(x => x.WorkorderCaseId == workOrderCase.ParentWorkorderCaseId).ToListAsync();

            foreach (var workorderCaseImage in parentCaseImages)
            {
                var uploadedData = await sdkDbContext.UploadedDatas.FirstAsync(x => x.Id == workorderCaseImage.UploadedDataId);
                picturesOfTasks.Add($"{uploadedData.Id}_700_{uploadedData.Checksum}{uploadedData.Extension}");
                var workOrderCaseImage = new WorkorderCaseImage
                {
                    WorkorderCaseId = workOrderCase.Id,
                    UploadedDataId = (int)uploadedData.Id
                };
                await workOrderCaseImage.Create(backendConfigurationPnDbContext);
            }

            var hash = await GeneratePdf(picturesOfTasks, site.Id);

            var label = $"<strong>{SharedResource.AssignedTo}:</strong> {assignedTo.Name}<br>";

            var pushMessageTitle = !string.IsNullOrEmpty(workOrderCase.SelectedAreaName) ? $"{property.Name}; {workOrderCase.SelectedAreaName}" : $"{property.Name}";
            var pushMessageBody = $"{commentFieldValue.Value}";
            var deviceUsersGroupUid = await sdkDbContext.EntityGroups
                .Where(x => x.Id == property.EntitySelectListDeviceUsers)
                .Select(x => x.MicrotingUid)
                .FirstAsync();
            if (textStatus != "Afsluttet")
            {
                label += $"<strong>{SharedResource.Location}:</strong> {property.Name}<br>" +
                         (!string.IsNullOrEmpty(workOrderCase.SelectedAreaName)
                             ? $"<strong>{SharedResource.Area}:</strong> {workOrderCase.SelectedAreaName}<br>"
                             : "") +
                         $"<strong>{SharedResource.Description}:</strong> {commentFieldValue.Value}<br><br>" +
                         $"<strong>{SharedResource.CreatedBy}:</strong> {workOrderCase.CreatedByName}<br>" +
                         (!string.IsNullOrEmpty(workOrderCase.CreatedByText)
                             ? $"<strong>{SharedResource.CreatedBy}:</strong> {workOrderCase.CreatedByText}<br>"
                             : "") +
                         $"<strong>{SharedResource.CreatedDate}:</strong> {workOrderCase.CaseInitiated: dd.MM.yyyy}<br><br>" +
                         $"<strong>{SharedResource.LastUpdatedBy}:</strong> {site.Name}<br>" +
                         $"<strong>{SharedResource.LastUpdatedDate}:</strong> {DateTime.UtcNow: dd.MM.yyyy}<br><br>" +
                         $"<strong>{SharedResource.Status}:</strong> {textStatus}<br><br>";
                // retract eform
                await RetractEform(workOrderCase);
                // deploy eform to ongoing status
                await DeployWorkOrderEform(propertyWorkers, eformIdForOngoingTasks, property, label,  CaseStatusesEnum.Ongoing, workOrderCase, commentFieldValue.Value, int.Parse(deviceUsersGroupUid), hash, assignedTo.Name, pushMessageBody, pushMessageTitle, updatedByName);
            }
            else
            {
                await RetractEform(workOrderCase);
            }
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
        string pdfHash,
        string siteName,
        string pushMessageBody,
        string pushMessageTitle,
        string updatedByName)
    {
        int? folderId = null;
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        await using var backendConfigurationPnDbContext =
            backendConfigurationDbContextHelper.GetDbContext();
        var i = 0;
        foreach (var propertyWorker in propertyWorkers)
        {
            var priorityText = "";

            var site = await sdkDbContext.Sites.FirstAsync(x => x.Id == propertyWorker.WorkerId);
            switch (workorderCase.Priority)
            {
                case "1":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Urgent}<br>";
                    break;
                case "2":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.High}<br>";
                    break;
                case "3":
                    priorityText = $"<strong>{SharedResource.Priority}:</strong> {SharedResource.Medium}<br>";
                    break;
                case "4":
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

            var assignedTo = site.Name == siteName ? "" : $"<strong>{SharedResource.AssignedTo}:</strong> {siteName}<br>";

            var areaName = !string.IsNullOrEmpty(workorderCase.SelectedAreaName)
                ? $"<strong>{SharedResource.Area}:</strong> {workorderCase.SelectedAreaName}<br>"
                : "";

            var outerDescription = $"<strong>{SharedResource.Location}:</strong> {property.Name}<br>" +
                                   areaName +
                                   $"<strong>{SharedResource.Description}:</strong> {newDescription}<br>" +
                                   priorityText +
                                   assignedTo +
                                   $"<strong>{SharedResource.Status}:</strong> {textStatus}<br><br>";
            var siteLanguage = await sdkDbContext.Languages.FirstAsync(x => x.Id == site.LanguageId);
            var mainElement = await sdkCore.ReadeForm(eformId, siteLanguage);
            mainElement.Label = " ";
            mainElement.ElementList[0].QuickSyncEnabled = true;
            mainElement.EnableQuickSync = true;
            mainElement.ElementList[0].Label = " ";
            mainElement.ElementList[0].Description.InderValue = outerDescription.Replace("\n", "<br>");
            if (status == CaseStatusesEnum.Completed || site.Name == siteName)
            {
                DateTime startDate = new DateTime(2020, 1, 1);
                mainElement.DisplayOrder = (int)(startDate - DateTime.UtcNow).TotalSeconds;
            }
            if (site.Name == siteName)
            {
                mainElement.CheckListFolderName = sdkDbContext.Folders.First(x => x.Id == (workorderCase.Priority != "1" ? property.FolderIdForOngoingTasks : property.FolderIdForTasks))
                    .MicrotingUid.ToString();
                folderId = property.FolderIdForOngoingTasks;
                mainElement.PushMessageTitle = pushMessageTitle;
                mainElement.PushMessageBody = pushMessageBody;
            }
            else
            {
                folderId = property.FolderIdForCompletedTasks;
                mainElement.CheckListFolderName = sdkDbContext.Folders.First(x => x.Id == property.FolderIdForCompletedTasks)
                    .MicrotingUid.ToString();
            }
            // TODO uncomment when new app has been released.
            ((DataElement)mainElement.ElementList[0]).DataItemList[0].Description.InderValue = description.Replace("\n", "<br>");
            ((DataElement)mainElement.ElementList[0]).DataItemList[0].Label = " ";
            ((DataElement)mainElement.ElementList[0]).DataItemList[0].Color = Constants.FieldColors.Yellow;
            ((ShowPdf) ((DataElement) mainElement.ElementList[0]).DataItemList[1]).Value = pdfHash;

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
            var caseId = await sdkCore.CaseCreate(mainElement, "", (int)site.MicrotingUid, folderId);
            await new WorkorderCase
            {
                CaseId = (int)caseId,
                PropertyWorkerId = propertyWorker.Id,
                CaseStatusesEnum = status,
                ParentWorkorderCaseId = workorderCase.Id,
                SelectedAreaName = workorderCase.SelectedAreaName,
                CreatedByName = workorderCase.CreatedByName,
                CreatedByText = workorderCase.CreatedByText,
                Description = newDescription,
                CaseInitiated = workorderCase.CaseInitiated,
                LastAssignedToName = siteName,
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

    private async Task<string> GeneratePdf(List<string> picturesOfTasks, int sitId)
    {
        var resourceString = "ServiceBackendConfigurationPlugin.Resources.Templates.page.html";
        var assembly = Assembly.GetExecutingAssembly();
        string html;
        await using (var resourceStream = assembly.GetManifestResourceStream(resourceString))
        {
            using var reader = new StreamReader(resourceStream ?? throw new InvalidOperationException($"{nameof(resourceStream)} is null"));
            html = await reader.ReadToEndAsync();
        }

        // Read docx stream
        resourceString = "ServiceBackendConfigurationPlugin.Resources.Templates.file.docx";
        var docxFileResourceStream = assembly.GetManifestResourceStream(resourceString);
        if (docxFileResourceStream == null)
        {
            throw new InvalidOperationException($"{nameof(docxFileResourceStream)} is null");
        }

        var docxFileStream = new MemoryStream();
        await docxFileResourceStream.CopyToAsync(docxFileStream);
        await docxFileResourceStream.DisposeAsync();
        string basePicturePath = Path.Combine(Path.GetTempPath(), "pictures", "workorders");
        Directory.CreateDirectory(basePicturePath);
        var word = new WordProcessor(docxFileStream);
        string imagesHtml = "";

        foreach (var imagesName in picturesOfTasks)
        {
            Console.WriteLine($"Trying to insert image into document : {imagesName}");
            imagesHtml = await InsertImageToPdf(imagesName, imagesHtml, 700, 650, basePicturePath);
        }

        html = html.Replace("{%Content%}", imagesHtml);

        word.AddHtml(html);
        word.Dispose();
        docxFileStream.Position = 0;

        // Build docx
        string downloadPath = Path.Combine(Path.GetTempPath(), "reports", "results");
        Directory.CreateDirectory(downloadPath);
        string timeStamp = DateTime.UtcNow.ToString("yyyyMMdd") + "_" + DateTime.UtcNow.ToString("hhmmss");
        string docxFileName = $"{timeStamp}{sitId}_temp.docx";
        string tempPDFFileName = $"{timeStamp}{sitId}_temp.pdf";
        string tempPDFFilePath = Path.Combine(downloadPath, tempPDFFileName);
        await using (var docxFile = new FileStream(Path.Combine(Path.GetTempPath(), "reports", "results", docxFileName), FileMode.Create, FileAccess.Write))
        {
            docxFileStream.WriteTo(docxFile);
        }

        // Convert to PDF
        ReportHelper.ConvertToPdf(Path.Combine(Path.GetTempPath(), "reports", "results", docxFileName), downloadPath);
        File.Delete(docxFileName);

        // Upload PDF
        // string pdfFileName = null;
        string hash = await sdkCore.PdfUpload(tempPDFFilePath);
        if (hash != null)
        {
            //rename local file
            FileInfo fileInfo = new FileInfo(tempPDFFilePath);
            fileInfo.CopyTo(downloadPath + "/" + hash + ".pdf", true);
            fileInfo.Delete();
            await sdkCore.PutFileToStorageSystem(Path.Combine(downloadPath, $"{hash}.pdf"), $"{hash}.pdf");

            // TODO Remove from file storage?


        }

        return hash;
    }

    private async Task<string> InsertImageToPdf(string imageName, string itemsHtml, int imageSize, int imageWidth, string basePicturePath)
    {
        if (imageName.Contains("GH"))
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceString = $"ServiceBackendConfigurationPlugin.Resources.GHSHazardPictogram.{imageName}.jpg";
            // using var FileStream FileStream = new FileStream()
            await using var resourceStream = assembly.GetManifestResourceStream(resourceString);
            // using var reader = new StreamReader(resourceStream ?? throw new InvalidOperationException($"{nameof(resourceStream)} is null"));
            // html = await reader.ReadToEndAsync();
            // MemoryStream memoryStream = new MemoryStream();
            // await resourceStream.CopyToAsync(memoryStream);
            using var image = new MagickImage(resourceStream);
            var profile = image.GetExifProfile();
            // Write all values to the console
            try
            {
                foreach (var value in profile.Values)
                {
                    Console.WriteLine("{0}({1}): {2}", value.Tag, value.DataType, value);
                }
            } catch (Exception)
            {
                // Console.WriteLine(e);
            }
            // image.Rotate(90);
            var base64String = image.ToBase64();
            itemsHtml +=
                $@"<p><img src=""data:image/png;base64,{base64String}"" width=""{imageWidth}px"" alt="""" /></p>";

            // await stream.DisposeAsync();
        }
        else
        {
            var storageResult = await sdkCore.GetFileFromS3Storage(imageName);
            var stream = storageResult.ResponseStream;

            using var image = new MagickImage(stream);
            var profile = image.GetExifProfile();
            // Write all values to the console
            try
            {
                foreach (var value in profile.Values)
                {
                    Console.WriteLine("{0}({1}): {2}", value.Tag, value.DataType, value);
                }
            } catch (Exception)
            {
                // Console.WriteLine(e);
            }
            image.Rotate(90);
            var base64String = image.ToBase64();
            itemsHtml +=
                $@"<p><img src=""data:image/png;base64,{base64String}"" width=""{imageWidth}px"" alt="""" /></p>";

            await stream.DisposeAsync();
        }

        return itemsHtml;
    }
}