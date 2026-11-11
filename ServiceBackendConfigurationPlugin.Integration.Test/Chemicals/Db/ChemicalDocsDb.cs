namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

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
