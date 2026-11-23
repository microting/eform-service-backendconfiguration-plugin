/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

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
namespace ServiceBackendConfigurationPlugin.Integration.Test.TaskReminders;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using TaskNotificationKinds = Microting.EformBackendConfigurationBase.Infrastructure.Const.TaskNotificationKinds;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

/// <summary>Each test starts with no reminders, no device tokens and no eForm credential row.</summary>
public abstract class TaskReminderTestBase
{
    protected static readonly BackendConfigurationDbContextHelper DbHelper =
        new(TaskReminderDatabases.ConnectionString(TaskReminderDatabases.Db));

    protected static readonly TaskReminderContextSource Contexts =
        new(TaskReminderDatabases.ConnectionString(TaskReminderDatabases.Db));

    protected DateTime Now { get; set; }
    protected FakeTaskReminderSender Sender { get; private set; }
    protected List<string> Logs { get; private set; }

    [SetUp]
    public async Task ClearAsync()
    {
        Now = new DateTime(2026, 11, 23, 6, 50, 0, DateTimeKind.Utc);
        Sender = new FakeTaskReminderSender();
        Logs = [];
        await using var db = DbHelper.GetDbContext();
        // Test data only, in a private test database.
        await db.TaskNotifications.ExecuteDeleteAsync();
        await db.DeviceTokenVersions.ExecuteDeleteAsync();
        await db.DeviceTokens.ExecuteDeleteAsync();
        await db.PluginConfigurationValues
            .Where(x => x.Name == TaskReminderDeliveryLoop.ServiceAccountJsonKey)
            .ExecuteDeleteAsync();
    }

    protected TaskReminderDeliverer Deliverer(FakeTaskReminderSender sender = null) =>
        new(Contexts.CreateAsync, sender ?? Sender, () => Now, Logs.Add);

    protected TaskReminderDeliveryLoop Loop(Func<CancellationToken, Task<BackendConfigurationPnDbContext>> createContext = null) =>
        new(createContext ?? Contexts.CreateAsync, Sender, () => Now, Logs.Add);

    protected Task<TaskReminderDeliverer.PassResult> RunAsync(TaskReminderDeliverer deliverer = null) =>
        (deliverer ?? Deliverer()).RunOnceAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

    /// <summary>A Pending reminder, due now, for a task that starts in <paramref name="startsIn"/>.</summary>
    protected async Task<TaskNotification> PlanAsync(
        int sdkSiteId = 31, int eventId = 7, string day = "2026-11-23", TimeSpan? startsIn = null, int? complianceId = 900,
        DateTime? createdAt = null)
    {
        var start = Now + (startsIn ?? TimeSpan.FromMinutes(10));
        await using var db = DbHelper.GetDbContext();
        await TaskNotificationOutbox.PlanAsync(db,
        [
            new TaskNotificationPlan(sdkSiteId, 42, eventId, day, complianceId, TaskNotificationKinds.TaskDue,
                start, start - TimeSpan.FromMinutes(10) < Now ? start - TimeSpan.FromMinutes(10) : Now,
                "Starter kl. 08:00", "Fodring af kalve · Gården")
        ], createdAt ?? Now);
        return await db.TaskNotifications.AsNoTracking().SingleAsync(x =>
            x.SdkSiteId == sdkSiteId && x.AreaRulePlanningId == eventId && x.PlanDayKey == day);
    }

    protected static async Task<TaskNotification> RowAsync(long id)
    {
        await using var db = DbHelper.GetDbContext();
        return await db.TaskNotifications.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    protected static async Task<DeviceToken> TokenAsync(
        int sdkSiteId, string fcmToken, string appId = TaskReminderDeliverer.EformAppId, bool taskReminders = true,
        bool removed = false)
    {
        await using var db = DbHelper.GetDbContext();
        var token = new DeviceToken
        {
            AppId = appId, InstallationId = Guid.NewGuid().ToString("N"), FcmToken = fcmToken, SdkSiteId = sdkSiteId,
            Platform = "android", TaskReminders = taskReminders,
        };
        await token.Create(db);
        if (removed)
        {
            await token.Delete(db);
        }

        return token;
    }

    protected static async Task<string> TokenStateAsync(int tokenId)
    {
        await using var db = DbHelper.GetDbContext();
        return await db.DeviceTokens.AsNoTracking().Where(x => x.Id == tokenId).Select(x => x.WorkflowState).SingleAsync();
    }

    protected static string Removed => Constants.WorkflowStates.Removed;
}
