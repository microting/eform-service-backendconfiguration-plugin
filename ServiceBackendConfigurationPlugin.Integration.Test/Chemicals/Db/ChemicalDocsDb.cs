namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Factories;
using Microsoft.EntityFrameworkCore;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Private chemical databases on the CI MariaDB (localhost:3306), created with EnsureCreated like
/// ChemicalJobDatabases. EnsureCreated writes no __EFMigrationsHistory, so <see cref="MarkMigrated"/> records the
/// migration the service's ChemicalSchemaGate requires; <see cref="MarkNotMigrated"/> removes it again.
/// </summary>
public static class ChemicalDocsDb
{
    private const int CommandTimeoutSeconds = 600;

    public static ChemicalDbContextHelper Helper(string database) =>
        new(ChemicalJobDatabases.ConnectionString(database));

    public static ChemicalsDbContext Open(string database) =>
        new ChemicalsContextFactory().CreateDbContext([ChemicalJobDatabases.ConnectionString(database)]);

    /// <summary>A fresh database with the full current schema and the migration recorded.</summary>
    public static void Create(string database)
    {
        using var db = Open(database);
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
        MarkMigrated(db);
    }

    /// <summary>Runs <paramref name="body"/> against a fresh private database and always drops it afterwards.</summary>
    public static async Task UseTemporaryDatabaseAsync(string database, Func<Task> body)
    {
        Create(database);
        try
        {
            await body();
        }
        finally
        {
            Drop(database);
        }
    }

    /// <summary>The plugin has not migrated yet: no ChemicalDocuments tables (MySQL 1146 on first use).</summary>
    public static async Task DropDocumentTablesAsync(string database)
    {
        await using var db = Open(database);
        await db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 0");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS `ChemicalDocumentVersions`");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS `ChemicalDocuments`");
    }

    public static void Drop(string database)
    {
        using var db = Open(database);
        db.Database.EnsureDeleted();
    }

    public static void MarkMigrated(ChemicalsDbContext db)
    {
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (`MigrationId` varchar(150) NOT NULL, `ProductVersion` varchar(32) NOT NULL, PRIMARY KEY (`MigrationId`))");
        db.Database.ExecuteSqlRaw(
            "INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES ({0}, '10.0.0')",
            ChemicalSchemaGate.RequiredMigration);
    }

    public static void MarkNotMigrated(ChemicalsDbContext db) =>
        db.Database.ExecuteSqlRaw("DELETE FROM `__EFMigrationsHistory` WHERE `MigrationId` = {0}",
            ChemicalSchemaGate.RequiredMigration);
}
