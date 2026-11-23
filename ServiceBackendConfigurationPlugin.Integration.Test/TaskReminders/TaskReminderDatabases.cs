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

using Microsoft.EntityFrameworkCore;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Factories;
using NUnit.Framework;

/// <summary>
/// Private databases for the task reminder tests, created once per run on the MariaDB the service CI
/// job already starts (localhost:3306), as <c>ChemicalJobDatabases</c> does. <see cref="NoTableDb"/>
/// is migrated and then loses its TaskNotifications table: a tenant whose web plugin has not migrated.
/// </summary>
[SetUpFixture]
public class TaskReminderDatabases
{
    public const string Db = "task-reminders-backend-configuration";
    public const string NoTableDb = "task-reminders-backend-configuration-no-table";

    private const int CommandTimeoutSeconds = 600;

    public static string ConnectionString(string database) =>
        $"Server = localhost; port = 3306; Database = {database}; user = root; password = secretpassword; Convert Zero Datetime = true;";

    [OneTimeSetUp]
    public void CreateDatabases()
    {
        foreach (var database in new[] { Db, NoTableDb })
        {
            using var db = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(database)]);
            db.Database.SetCommandTimeout(CommandTimeoutSeconds);
            db.Database.EnsureDeleted();
            db.Database.Migrate();
        }

        using var noTable = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(NoTableDb)]);
        noTable.Database.ExecuteSqlRaw("DROP TABLE `TaskNotifications`");
    }

    [OneTimeTearDown]
    public void DropDatabases()
    {
        foreach (var database in new[] { Db, NoTableDb })
        {
            using var db = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(database)]);
            db.Database.EnsureDeleted();
        }
    }
}
