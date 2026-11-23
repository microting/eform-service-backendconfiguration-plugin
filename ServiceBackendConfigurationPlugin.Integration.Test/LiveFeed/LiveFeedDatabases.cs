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

using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Factories;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Factories;
using Microting.ItemsPlanningBase.Infrastructure.Data.Factories;
using NUnit.Framework;

/// <summary>
/// Private databases for the task change feed tests, created once per run on the MariaDB the
/// service CI job already starts (localhost:3306), as <c>ChemicalJobDatabases</c> does. Never
/// touches the 420_* databases the Docker startup step relies on. <see cref="NoFeedTableDb"/> is a
/// migrated backend-configuration database whose feed table is dropped again: a tenant whose web
/// plugin has not migrated to the feed yet.
/// </summary>
[SetUpFixture]
public class LiveFeedDatabases
{
    public const string BackendConfigurationDb = "live-feed-backend-configuration";
    public const string NoFeedTableDb = "live-feed-backend-configuration-no-table";
    public const string ItemsPlanningDb = "live-feed-items-planning";
    public const string SdkDb = "live-feed-sdk";

    private const int CommandTimeoutSeconds = 600;

    public static string ConnectionString(string database) =>
        $"Server = localhost; port = 3306; Database = {database}; user = root; password = secretpassword; Convert Zero Datetime = true;";

    [OneTimeSetUp]
    public void CreateDatabases()
    {
        foreach (var database in new[] { BackendConfigurationDb, NoFeedTableDb })
        {
            using var backendConfiguration = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(database)]);
            backendConfiguration.Database.SetCommandTimeout(CommandTimeoutSeconds);
            backendConfiguration.Database.EnsureDeleted();
            backendConfiguration.Database.Migrate();
        }

        using (var noTable = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(NoFeedTableDb)]))
        {
            noTable.Database.ExecuteSqlRaw("DROP TABLE `TaskChangeFeedEntries`");
        }

        using (var itemsPlanning = new ItemsPlanningPnContextFactory().CreateDbContext([ConnectionString(ItemsPlanningDb)]))
        {
            itemsPlanning.Database.SetCommandTimeout(CommandTimeoutSeconds);
            itemsPlanning.Database.EnsureDeleted();
            itemsPlanning.Database.Migrate();
        }

        using (var sdk = new MicrotingDbContextFactory().CreateDbContext([ConnectionString(SdkDb)]))
        {
            sdk.Database.SetCommandTimeout(CommandTimeoutSeconds);
            sdk.Database.EnsureDeleted();
            sdk.Database.Migrate();
        }
    }

    [OneTimeTearDown]
    public void DropDatabases()
    {
        foreach (var database in new[] { BackendConfigurationDb, NoFeedTableDb })
        {
            using var backendConfiguration = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(database)]);
            backendConfiguration.Database.EnsureDeleted();
        }

        using var itemsPlanning = new ItemsPlanningPnContextFactory().CreateDbContext([ConnectionString(ItemsPlanningDb)]);
        itemsPlanning.Database.EnsureDeleted();
        using var sdk = new MicrotingDbContextFactory().CreateDbContext([ConnectionString(SdkDb)]);
        sdk.Database.EnsureDeleted();
    }
}
