namespace ServiceBackendConfigurationPlugin.Integration.Test.ShiftedLookupIds;

using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Factories;
using NUnit.Framework;

/// <summary>
/// A private SDK database on the CI MariaDB (localhost:3306) whose lookup tables
/// carry the ids a fresh MariaDB Galera install (auto_increment_increment=3)
/// produces: Dansk=3, English=6, Deutsch=9, SingleSelect=24. Nothing has id 1,
/// so any code that assumes "Danish is LanguageId 1" finds nothing.
/// </summary>
[SetUpFixture]
public class ShiftedLookupIdsDatabases
{
    public const int DanishId = 3;
    public const int EnglishId = 6;
    public const int GermanId = 9;
    public const int SingleSelectFieldTypeId = 24;

    private const string SdkDb = "shifted-ids-sdk";
    private const int CommandTimeoutSeconds = 600;

    public static MicrotingDbContext Sdk() =>
        new MicrotingDbContextFactory().CreateDbContext(
            [$"Server = localhost; port = 3306; Database = {SdkDb}; user = root; password = secretpassword; Convert Zero Datetime = true;"]);

    [OneTimeSetUp]
    public void CreateDatabase()
    {
        using var sdk = Sdk();
        sdk.Database.SetCommandTimeout(CommandTimeoutSeconds);
        sdk.Database.EnsureDeleted();
        sdk.Database.Migrate();
        sdk.Languages.AddRange(
            new Language { Id = DanishId, Name = "Dansk", LanguageCode = "da" },
            new Language { Id = EnglishId, Name = "English", LanguageCode = "en-US" },
            new Language { Id = GermanId, Name = "Deutsch", LanguageCode = "de-DE" });
        sdk.FieldTypes.Add(new FieldType { Id = SingleSelectFieldTypeId, Type = "SingleSelect" });
        sdk.SaveChanges();
    }

    [OneTimeTearDown]
    public void DropDatabase()
    {
        using var sdk = Sdk();
        sdk.Database.EnsureDeleted();
    }
}
