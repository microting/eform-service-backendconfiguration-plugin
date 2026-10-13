namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using ChemicalsBase.Infrastructure.Data.Factories;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Factories;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Factories;
using NUnit.Framework;

/// <summary>
/// Creates the three private databases the chemical job tests use, once per
/// test run, on the MariaDB the service CI job already starts (localhost:3306).
/// Never touches the 420_* databases the Docker startup step relies on.
/// </summary>
[SetUpFixture]
public class ChemicalJobDatabases
{
    public const string BackendConfigurationDb = "chemical-jobs-backend-configuration";
    public const string ChemicalsDb = "chemical-jobs-chemicals";
    public const string SdkDb = "chemical-jobs-sdk";

    private const int CommandTimeoutSeconds = 600;

    public static string ConnectionString(string database) =>
        $"Server = localhost; port = 3306; Database = {database}; user = root; password = secretpassword; Convert Zero Datetime = true;";

    [OneTimeSetUp]
    public void CreateDatabases()
    {
        using (var backendConfiguration = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(BackendConfigurationDb)]))
        {
            backendConfiguration.Database.SetCommandTimeout(CommandTimeoutSeconds);
            backendConfiguration.Database.EnsureDeleted();
            backendConfiguration.Database.Migrate();
        }

        using (var chemicals = new ChemicalsContextFactory().CreateDbContext([ConnectionString(ChemicalsDb)]))
        {
            chemicals.Database.SetCommandTimeout(CommandTimeoutSeconds);
            chemicals.Database.EnsureDeleted();
            chemicals.Database.EnsureCreated();
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
        using var backendConfiguration = new BackendConfigurationPnContextFactory().CreateDbContext([ConnectionString(BackendConfigurationDb)]);
        backendConfiguration.Database.EnsureDeleted();
        using var chemicals = new ChemicalsContextFactory().CreateDbContext([ConnectionString(ChemicalsDb)]);
        chemicals.Database.EnsureDeleted();
        using var sdk = new MicrotingDbContextFactory().CreateDbContext([ConnectionString(SdkDb)]);
        sdk.Database.EnsureDeleted();
    }
}
