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

namespace ServiceBackendConfigurationPlugin.Integration.Test.LiveFeed;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Shared seeding for the feed tests. Each test starts with an empty feed and its own property, so
/// rows from another test cannot satisfy an assertion.
/// </summary>
public abstract class LiveFeedTestBase
{
    protected static readonly BackendConfigurationDbContextHelper DbHelper =
        new(LiveFeedDatabases.ConnectionString(LiveFeedDatabases.BackendConfigurationDb));

    protected List<string> Warnings { get; private set; }
    protected List<string> Debugs { get; private set; }
    protected DateTime Now { get; set; }

    [SetUp]
    public async Task ClearFeedAsync()
    {
        Warnings = [];
        Debugs = [];
        Now = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        await using var db = DbHelper.GetDbContext();
        // Test rows only, in a private test database.
        await db.TaskChangeFeedEntries.ExecuteDeleteAsync();
    }

    /// <summary>
    /// A writer that records its log lines: the production writer on the test database, or one on
    /// <paramref name="createContext"/> for the failure cases.
    /// </summary>
    protected TaskChangeFeedWriter Writer(Func<BackendConfigurationPnDbContext> createContext = null) =>
        createContext == null
            ? new TaskChangeFeedWriter(
                LiveFeedDatabases.ConnectionString(LiveFeedDatabases.BackendConfigurationDb), Warnings.Add, Debugs.Add, () => Now)
            : new TaskChangeFeedWriter(createContext, Warnings.Add, Debugs.Add, () => Now);

    protected static async Task<List<TaskChangeFeedEntry>> FeedRowsAsync()
    {
        await using var db = DbHelper.GetDbContext();
        return await db.TaskChangeFeedEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }

    protected static async Task<Property> CreatePropertyAsync(BackendConfigurationPnDbContext db)
    {
        var property = new Property
        {
            Name = "Gården", Address = "Markvej 1", CHR = Guid.NewGuid().ToString("N")[..8],
            CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await property.Create(db);
        return property;
    }

    /// <summary>Area, area rule and task (AreaRulePlanning) of <paramref name="propertyId"/> for one items-planning planning.</summary>
    protected static async Task<AreaRulePlanning> CreateTaskAsync(
        BackendConfigurationPnDbContext db, int propertyId, int itemPlanningId, bool removed = false,
        bool complianceEnabled = true)
    {
        var area = new Area { CreatedByUserId = 1, UpdatedByUserId = 1 };
        await area.Create(db);
        var areaRule = new AreaRule
        {
            AreaId = area.Id, PropertyId = propertyId, CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await areaRule.Create(db);
        var areaRulePlanning = new AreaRulePlanning
        {
            AreaRuleId = areaRule.Id, AreaId = area.Id, PropertyId = propertyId, ItemPlanningId = itemPlanningId,
            ComplianceEnabled = complianceEnabled, Status = true, CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await areaRulePlanning.Create(db);
        if (removed)
        {
            await areaRulePlanning.Delete(db);
        }

        return areaRulePlanning;
    }

    protected static int NextPlanningId() => Random.Shared.Next(100_000, 900_000_000);
}
