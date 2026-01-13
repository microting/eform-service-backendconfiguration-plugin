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


using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Factories;
using Microting.eForm.Infrastructure.Models;
using Microting.EformAngularFrontendBase.Infrastructure.Data;
using Microting.EformAngularFrontendBase.Infrastructure.Data.Factories;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.eFormCaseTemplateBase.Infrastructure.Data.Factories;
using QuestPDF.Infrastructure;
using Sentry;
using ServiceBackendConfigurationPlugin.Resources;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

namespace ServiceBackendConfigurationPlugin;

using Castle.MicroKernel.Registration;
using Castle.Windsor;
using Infrastructure.Helpers;
using Installers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Dto;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Factories;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Factories;
using Microting.WindowsService.BasePn;
using Rebus.Bus;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

[Export(typeof(ISdkEventHandler))]
public class Core : ISdkEventHandler
{
    private eFormCore.Core _sdkCore;
    private IWindsorContainer _container;
    private IBus _bus;
    private bool _coreThreadRunning = false;
    private bool _coreStatChanging;
    private bool _coreAvailable;
    private string _serviceLocation;
    private static int _maxParallelism = 1;
    private static int _numberOfWorkers = 1;
    private BackendConfigurationPnDbContext _dbContext;
    private Timer _scheduleTimer;
    private ItemsPlanningPnDbContext _itemsPlanningDbContext;
    private BackendConfigurationDbContextHelper _backendConfigurationBackendConfigurationDbContextHelper;
    private ItemsPlanningDbContextHelper _itemsPlanningDbContextHelper;
    private ChemicalsDbContext _chemicalsDbContext;
    private ChemicalDbContextHelper _chemicalDbContextHelper;
    private CaseTemplateDbContextHelper _caseTemplateDbContextHelper;
    private BaseDbContext _baseDbContext;

    public void CoreEventException(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void UnitActivated(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void eFormProcessed(object sender, EventArgs args)
    {
        var trigger = (CaseDto) sender;

        if (trigger.MicrotingUId != null)
        {
            _bus.SendLocal(new EformParsedByServer(trigger.CaseId, trigger.MicrotingUId, trigger.CheckUId));
        }
    }

    public void eFormProcessingError(object sender, EventArgs args)
    {
        // CaseDto trigger = (CaseDto)sender;
        //
        // int? caseId = trigger.MicrotingUId;
        // if (caseId != null) _bus.SendLocal(new EformParsingError((int) caseId));
    }

    public void eFormRetrived(object sender, EventArgs args)
    {
        CaseDto trigger = (CaseDto)sender;

        // int? caseId = trigger.MicrotingUId;
        if (trigger.CaseId != null) _bus.SendLocal(new eFormRetrieved((int) trigger.CaseId));
    }

    public void CaseCompleted(object sender, EventArgs args)
    {
        var trigger = (CaseDto) sender;

        if (trigger.MicrotingUId != null)
        {
            _bus.SendLocal(new eFormCompleted(trigger.CaseId, trigger.MicrotingUId, trigger.CheckUId,
                trigger.SiteUId));
        }
    }

    public void CaseDeleted(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void NotificationNotFound(object sender, EventArgs args)
    {
        // Do nothing
    }

    public bool Start(string sdkConnectionString, string serviceLocation)
    {
        SentrySdk.Init(options =>
        {
            // A Sentry Data Source Name (DSN) is required.
            // See https://docs.sentry.io/product/sentry-basics/dsn-explainer/
            // You can set it in the SENTRY_DSN environment variable, or you can set it in code here.
            options.Dsn = "https://5b67d97e4f88df38b0bbfcd8ed3b9a65@o4506241219428352.ingest.us.sentry.io/4508153878282240";

            // When debug is enabled, the Sentry client will emit detailed debugging information to the console.
            // This might be helpful, or might interfere with the normal operation of your application.
            // We enable it here for demonstration purposes when first trying Sentry.
            // You shouldn't do this in your applications unless you're troubleshooting issues with Sentry.
            options.Debug = false;

            // This option is recommended. It enables Sentry's "Release Health" feature.
            options.AutoSessionTracking = true;

            // Enabling this option is recommended for client applications only. It ensures all threads use the same global scope.
            options.IsGlobalModeEnabled = false;

            // Example sample rate for your transactions: captures 10% of transactions
            options.TracesSampleRate = 0.1;
        });
        QuestPDF.Settings.License = LicenseType.Community;

        Console.WriteLine("ServiceBackendConfigurationPlugin start called");
        try
        {
            var dbNameSection = Regex.Match(sdkConnectionString, @"(Database=\w*;)").Groups[0].Value;
            var dbPrefix = Regex.Match(sdkConnectionString, @"Database=(\d*)_").Groups[1].Value;

            int number = int.Parse(dbPrefix);
            SentrySdk.ConfigureScope(scope =>
            {
                scope.SetTag("customerNo", number.ToString());
                Console.WriteLine("info: customerNo: " + number);
                scope.SetTag("osVersion", Environment.OSVersion.ToString());
                Console.WriteLine("info: osVersion: " + Environment.OSVersion);
                scope.SetTag("osArchitecture", RuntimeInformation.OSArchitecture.ToString());
                Console.WriteLine("info: osArchitecture: " + RuntimeInformation.OSArchitecture);
                scope.SetTag("osName", RuntimeInformation.OSDescription);
                Console.WriteLine("info: osName: " + RuntimeInformation.OSDescription);
            });

            var pluginDbName = $"Database={dbPrefix}_eform-backend-configuration-plugin;";
            var connectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);

            if (!_coreAvailable && !_coreStatChanging)
            {
                _serviceLocation = serviceLocation;
                _coreStatChanging = true;

                if (string.IsNullOrEmpty(_serviceLocation))
                    throw new ArgumentException("serviceLocation is not allowed to be null or empty");

                if (string.IsNullOrEmpty(connectionString))
                    throw new ArgumentException("serverConnectionString is not allowed to be null or empty");

                var contextFactory = new BackendConfigurationPnContextFactory();

                _dbContext = contextFactory.CreateDbContext(new[] {connectionString});
                _dbContext.Database.Migrate();
                _backendConfigurationBackendConfigurationDbContextHelper =
                    new BackendConfigurationDbContextHelper(connectionString);

                pluginDbName = $"Database={dbPrefix}_eform-angular-items-planning-plugin;";
                var itemsPlanningConnectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);
                var itemContextFactory = new ItemsPlanningPnContextFactory();

                _itemsPlanningDbContext =
                    itemContextFactory.CreateDbContext(new[] {itemsPlanningConnectionString});
                _itemsPlanningDbContextHelper = new ItemsPlanningDbContextHelper(itemsPlanningConnectionString);


                pluginDbName = $"Database={dbPrefix}_chemical-base-plugin;";
                var chemicalConnectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);
                var chemicalContextFactory = new ChemicalsContextFactory();


                var angularDbName = $"Database={dbPrefix}_Angular;";
                var angularConnectionString = sdkConnectionString.Replace(dbNameSection, angularDbName);
                _baseDbContext = new BaseDbContextFactory().CreateDbContext(new[] {angularConnectionString});


                pluginDbName = $"Database={dbPrefix}_eform-angular-case-template-plugin;";

                var caseTemplateConnectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);
                var caseTemplateContextFactory = new CaseTemplatePnContextFactory();

                _chemicalsDbContext =
                    chemicalContextFactory.CreateDbContext(new[] {chemicalConnectionString});
                _chemicalDbContextHelper = new ChemicalDbContextHelper(chemicalConnectionString);

                _caseTemplateDbContextHelper = new CaseTemplateDbContextHelper(caseTemplateConnectionString);

                var caseTemplateDbContext =
                    caseTemplateContextFactory.CreateDbContext(new[] {caseTemplateConnectionString});
                caseTemplateDbContext.Database.Migrate();

                _coreAvailable = true;
                _coreStatChanging = false;

                StartSdkCoreSqlOnly(sdkConnectionString);
                Console.WriteLine($"Connection string: {sdkConnectionString}");

                var rabbitmqHost = _sdkCore.GetSdkSetting(Settings.rabbitMqHost).GetAwaiter().GetResult();
                Console.WriteLine($"info: rabbitmqHost: {rabbitmqHost}");
                var rabbitMqUser = _sdkCore.GetSdkSetting(Settings.rabbitMqUser).GetAwaiter().GetResult();
                Console.WriteLine($"info: rabbitMqUser: {rabbitMqUser}");
                var rabbitMqPassword = _sdkCore.GetSdkSetting(Settings.rabbitMqPassword).GetAwaiter().GetResult();
                Console.WriteLine($"info: rabbitMqPassword: {rabbitMqPassword}");

                // var rabbitmqHost = connectionString.Contains("frontend")
                //     ? $"frontend-{dbPrefix}-rabbitmq"
                //     : "localhost";

                var temp = _dbContext.PluginConfigurationValues
                    .SingleOrDefault(x => x.Name == "BackendConfigurationBaseSettings:MaxParallelism")?.Value;
                _maxParallelism = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);

                temp = _dbContext.PluginConfigurationValues
                    .SingleOrDefault(x => x.Name == "BackendConfigurationBaseSettings:NumberOfWorkers")?.Value;
                _numberOfWorkers = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);

                // CheckComplianceIntegrity(connectionString, itemsPlanningConnectionString);

                _container = new WindsorContainer();
                _container.Register(Component.For<IWindsorContainer>().Instance(_container));
                _container.Register(Component.For<BackendConfigurationDbContextHelper>()
                    .Instance(_backendConfigurationBackendConfigurationDbContextHelper));
                _container.Register(Component.For<ItemsPlanningDbContextHelper>()
                    .Instance(_itemsPlanningDbContextHelper));
                _container.Register(Component.For<ChemicalDbContextHelper>()
                    .Instance(_chemicalDbContextHelper));
                _container.Register(Component.For<CaseTemplateDbContextHelper>()
                    .Instance(_caseTemplateDbContextHelper));
                _container.Register(Component.For<eFormCore.Core>().Instance(_sdkCore));
                _container.Register(Component.For<BaseDbContext>().Instance(_baseDbContext));
                _container.Install(
                    new RebusHandlerInstaller()
                    , new RebusInstaller(dbPrefix, connectionString, _maxParallelism, _numberOfWorkers, rabbitMqUser, rabbitMqPassword, rabbitmqHost)
                );
                _container.Register(Component.For<SearchListJob>());

                _bus = _container.Resolve<IBus>();

                ConfigureScheduler();
                //ReplaceCreateTask(connectionString, sdkConnectionString).GetAwaiter().GetResult();
            }

            Console.WriteLine("info: ServiceBackendConfigurationPlugin started");
            return true;
        }
        catch (Exception ex)
        {
            var color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine("fail: Start failed " + ex.Message);
            Console.ForegroundColor = color;
            throw;
        }
    }

    public bool Stop(bool shutdownReallyFast)
    {
        if (_coreAvailable && !_coreStatChanging)
        {
            _coreStatChanging = true;

            _coreAvailable = false;

            while (_coreThreadRunning)
            {
                Thread.Sleep(100);
                _bus.Dispose();
            }

            _sdkCore.Close().GetAwaiter().GetResult();

            _coreStatChanging = false;
        }

        return true;
    }

    public bool Restart(int sameExceptionCount, int sameExceptionCountMax, bool shutdownReallyFast)
    {
        return true;
    }

    private void StartSdkCoreSqlOnly(string sdkConnectionString)
    {
        _sdkCore = new eFormCore.Core();

        _sdkCore.StartSqlOnly(sdkConnectionString).GetAwaiter().GetResult();
    }

    private void CheckComplianceIntegrity(string connectionStringBackend, string connectionStringItemsPlanning)
    {
        var contextFactory = new BackendConfigurationPnContextFactory();
        var backendConfigurationPnDbContext = contextFactory.CreateDbContext(new[] {connectionStringBackend});

        var contextFactoryItemsPlanning = new ItemsPlanningPnContextFactory();
        var itemsPlanningPnDbContext =
            contextFactoryItemsPlanning.CreateDbContext(new[] {connectionStringItemsPlanning});

        var properties = backendConfigurationPnDbContext.Properties
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .ToList();

        foreach (var property in properties)
        {
            var backendPlannings = backendConfigurationPnDbContext.AreaRulePlannings
                .Where(x => x.PropertyId == property.Id)
                .Where(x => x.ItemPlanningId != 0)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .ToList();

            foreach (var areaRulePlanning in backendPlannings)
            {
                if (backendConfigurationPnDbContext.Compliances.Any(x =>
                        x.PlanningId == areaRulePlanning.ItemPlanningId &&
                        x.WorkflowState != Constants.WorkflowStates.Removed))
                {
                    continue;
                }

                var planningCases =
                    itemsPlanningPnDbContext.PlanningCases
                        .Where(x => x.PlanningId == areaRulePlanning.ItemPlanningId)
                        .Where(x => x.Status != 100)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Retracted)
                        .ToList();

                foreach (var planningCase in planningCases)
                {
                    var planning =
                        itemsPlanningPnDbContext.Plannings
                            .Where(x => x.Id == planningCase.PlanningId)
                            .Where(x => x.RepeatEvery != 0)
                            .SingleOrDefault(x => x.StartDate < DateTime.UtcNow);

                    if (planning == null)
                    {
                        continue;
                    }

                    try
                    {
                        var compliance = new Compliance
                        {
                            PlanningId = planning.Id,
                            AreaId = areaRulePlanning.AreaId,
                            Deadline = (DateTime) planning.NextExecutionTime!,
                            StartDate = (DateTime) planning.LastExecutedTime!,
                            MicrotingSdkCaseId = planningCase.MicrotingSdkCaseId,
                            MicrotingSdkeFormId = planning.RelatedEFormId,
                            PropertyId = property.Id
                        };

                        compliance.Create(backendConfigurationPnDbContext).GetAwaiter().GetResult();
                    }
                    catch (Exception)
                    {
                        // Console.WriteLine(e);
                        // throw;
                    }
                }
            }

            if (backendConfigurationPnDbContext.Compliances.Any(x =>
                    x.PropertyId == property.Id && x.Deadline < DateTime.UtcNow &&
                    x.WorkflowState != Constants.WorkflowStates.Removed))
            {
                property.ComplianceStatusThirty = 2;
                property.ComplianceStatus = 2;
            }
            else
            {
                if (backendConfigurationPnDbContext.Compliances.Any(x =>
                        x.PropertyId == property.Id && x.WorkflowState != Constants.WorkflowStates.Removed))
                {
                    property.ComplianceStatusThirty = backendConfigurationPnDbContext.Compliances.Any(x =>
                        x.Deadline < DateTime.UtcNow.AddDays(30) && x.PropertyId == property.Id &&
                        x.WorkflowState != Constants.WorkflowStates.Removed)
                        ? 1
                        : 0;
                    property.ComplianceStatus = 1;
                }
                else
                {
                    property.ComplianceStatusThirty = 0;
                    property.ComplianceStatus = 0;
                }
            }

            property.Update(backendConfigurationPnDbContext).GetAwaiter().GetResult();
        }
    }

    private void ConfigureScheduler()
    {
        var job = _container.Resolve<SearchListJob>();

        async void Callback(object x)
        {
            await job.Execute();
        }

        _scheduleTimer = new Timer(Callback, null, TimeSpan.Zero, TimeSpan.FromMinutes(60));
    }

    private async Task ReplaceCreateTask(string connectionStringBackend, string sdkConnectionString)
    {
        var sdkCore = new eFormCore.Core();

        sdkCore.StartSqlOnly(sdkConnectionString).GetAwaiter().GetResult();

        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();

        var contextFactory = new BackendConfigurationPnContextFactory();
        var backendConfigurationPnDbContext = contextFactory.CreateDbContext(new[] { connectionStringBackend });


        var properties = await backendConfigurationPnDbContext.Properties.Where(x => x.WorkorderEnable)
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .ToListAsync();

        var eformId = await sdkDbContext.CheckLists
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.OriginalId == "142663new2")
            .FirstOrDefaultAsync().ConfigureAwait(false);

        if (eformId != null)
        {
            foreach (var property in properties)
            {
                var propertyWorkers = await backendConfigurationPnDbContext.PropertyWorkers
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.PropertyId == property.Id).ToListAsync();
                var areasGroupUid = await sdkDbContext.EntityGroups
                    .Where(x => x.Id == property.EntitySelectListAreas)
                    .Select(x => x.MicrotingUid)
                    .SingleAsync().ConfigureAwait(false);

                var deviceUsersGroupUid = await sdkDbContext.EntityGroups
                    .Where(x => x.Id == property.EntitySelectListDeviceUsers)
                    .Select(x => x.MicrotingUid)
                    .SingleAsync().ConfigureAwait(false);

                foreach (var propertyWorker in propertyWorkers)
                {
                    var workorderCase = await backendConfigurationPnDbContext.WorkorderCases
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(x => x.PropertyWorkerId == propertyWorker.Id)
                        .Where(x => x.CaseStatusesEnum == CaseStatusesEnum.NewTask)
                        .FirstOrDefaultAsync().ConfigureAwait(false);
                    if (workorderCase != null)
                    {

                        var dbCase =
                            await sdkDbContext.CheckListSites.FirstAsync(
                                x => x.MicrotingUid == workorderCase.CaseId);

                        if (dbCase.CheckListId != eformId.Id || dbCase.FolderId != property.FolderIdForNewTasks)
                        {
                            try
                            {
                                await _sdkCore.CaseDelete(workorderCase.CaseId).ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e);
                                // throw;
                            }

                            await workorderCase.Delete(backendConfigurationPnDbContext).ConfigureAwait(false);

                            await DeployEform(propertyWorker, eformId.Id, property,
                                $"<strong>{SharedResource.Location}:</strong> {property.Name}",
                                int.Parse(areasGroupUid), int.Parse(deviceUsersGroupUid)).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }

    private async Task DeployEform(PropertyWorker propertyWorker, int eformId,
        Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities.Property property,
        string description, int? areasGroupUid, int? deviceUsersGroupId)
    {
        // var core = await co.GetCore().ConfigureAwait(false);
        var sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();
        // await using var _ = sdkDbContext.ConfigureAwait(false);
        await using var _backendConfigurationPnDbContext =
            _backendConfigurationBackendConfigurationDbContextHelper.GetDbContext();
        if (_backendConfigurationPnDbContext.WorkorderCases.Any(x =>
                x.PropertyWorkerId == propertyWorker.Id
                && x.CaseStatusesEnum == CaseStatusesEnum.NewTask
                && x.WorkflowState != Constants.WorkflowStates.Removed))
        {
            return;
        }

        var site = await sdkDbContext.Sites.SingleAsync(x => x.Id == propertyWorker.WorkerId).ConfigureAwait(false);
        var language = await sdkDbContext.Languages.SingleAsync(x => x.Id == site.LanguageId).ConfigureAwait(false);
        var mainElement = await _sdkCore.ReadeForm(eformId, language).ConfigureAwait(false);
        mainElement.Repeated = 0;
        mainElement.ElementList[0].QuickSyncEnabled = true;
        mainElement.ElementList[0].Description.InderValue = description;
        mainElement.ElementList[0].Label = "Ny opgave";
        mainElement.Label = "Ny opgave";
        mainElement.EnableQuickSync = true;
        if (property.FolderIdForNewTasks != null)
        {
            mainElement.CheckListFolderName = await sdkDbContext.Folders
                .Where(x => x.Id == property.FolderIdForNewTasks)
                .Select(x => x.MicrotingUid.ToString())
                .FirstOrDefaultAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(description))
        {
            ((DataElement) mainElement.ElementList[0]).DataItemList[0].Description.InderValue = description;
            ((DataElement) mainElement.ElementList[0]).DataItemList[0].Label = " ";
        }

        if (areasGroupUid != null && deviceUsersGroupId != null)
        {
            ((EntitySelect) ((DataElement) mainElement.ElementList[0]).DataItemList[2]).Source =
                (int) areasGroupUid;
            ((EntitySelect) ((DataElement) mainElement.ElementList[0]).DataItemList[6]).Source =
                (int) deviceUsersGroupId;
        }
        // else if (areasGroupUid == null && deviceUsersGroupId != null)
        // {
        //     ((EntitySelect)((DataElement)mainElement.ElementList[0]).DataItemList[4]).Source =
        //         (int)deviceUsersGroupId;
        // }

        mainElement.EndDate = DateTime.Now.AddYears(10).ToUniversalTime();
        mainElement.StartDate = DateTime.Now.ToUniversalTime();
        var caseId = await _sdkCore
            .CaseCreate(mainElement, "", (int) site.MicrotingUid, property.FolderIdForNewTasks)
            .ConfigureAwait(false);
        await new WorkorderCase
        {
            CaseId = (int) caseId,
            PropertyWorkerId = propertyWorker.Id,
            CaseStatusesEnum = CaseStatusesEnum.NewTask
            // CreatedByUserId = _userService.UserId,
            // UpdatedByUserId = _userService.UserId,
        }.Create(_backendConfigurationPnDbContext).ConfigureAwait(false);
    }
}