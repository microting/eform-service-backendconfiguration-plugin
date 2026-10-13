namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Linq;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microting.eFormApi.BasePn.Infrastructure.Database.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Real MariaDB (see ChemicalJobDatabases). Each test starts from empty
/// inventory tables; register rows are never cleared because every test
/// seeds its own chemicals and only references their ids.
/// </summary>
[NonParallelizable]
public abstract class ChemicalJobTestBase
{
    protected static readonly BackendConfigurationDbContextHelper DbHelper =
        new(ChemicalJobDatabases.ConnectionString(ChemicalJobDatabases.BackendConfigurationDb));

    protected static readonly ChemicalDbContextHelper ChemicalHelper =
        new(ChemicalJobDatabases.ConnectionString(ChemicalJobDatabases.ChemicalsDb));

    protected BackendConfigurationPnDbContext Db { get; private set; }

    private ChemicalsDbContext _chemicals;

    [SetUp]
    public async Task OpenAndClearAsync()
    {
        Db = DbHelper.GetDbContext();
        _chemicals = ChemicalHelper.GetDbContext();
        await Db.ChemicalAlertLogs.ExecuteDeleteAsync();
        await Db.ChemicalStockEntries.ExecuteDeleteAsync();
        await Db.ChemicalPlacements.ExecuteDeleteAsync();
        await Db.ChemicalLocations.ExecuteDeleteAsync();
        await Db.ChemicalPropertySettings.ExecuteDeleteAsync();
        await Db.ChemicalWorkerPermissions.ExecuteDeleteAsync();
        await Db.DeviceTokens.ExecuteDeleteAsync();
        await Db.PropertyWorkers.ExecuteDeleteAsync();
        await Db.Properties.ExecuteDeleteAsync();
        await Db.PluginConfigurationValues
            .Where(x => x.Name.StartsWith("BackendConfigurationSettings:Chemistry"))
            .ExecuteDeleteAsync();
    }

    [TearDown]
    public async Task CloseAsync()
    {
        await Db.DisposeAsync();
        await _chemicals.DisposeAsync();
    }

    /// <summary>A new context for assertions: the jobs write through their own contexts.</summary>
    protected static BackendConfigurationPnDbContext FreshDb() => DbHelper.GetDbContext();

    protected async Task<Property> CreatePropertyAsync(string name = "Gården")
    {
        var property = new Property
        {
            Name = name, Address = "Markvej 1", CHR = Guid.NewGuid().ToString("N")[..8],
            CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await property.Create(Db);
        return property;
    }

    protected async Task<ChemicalLocation> CreateLocationAsync(int propertyId, string name = "Kemirum")
    {
        var location = new ChemicalLocation
        {
            PropertyId = propertyId, Name = name, SortOrder = 1, CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await location.Create(Db);
        return location;
    }

    protected async Task<ChemicalPlacement> CreatePlacementAsync(
        int locationId, int chemicalId, int? observedStatus = 5, int? movedFromPlacementId = null,
        DateTime? registeredAt = null, string note = "Hylde 2")
    {
        var placement = new ChemicalPlacement
        {
            LocationId = locationId, ChemicalId = chemicalId, PlacementNote = note,
            RegisteredByUserId = 3, RegisteredAt = registeredAt ?? new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            ObservedStatus = observedStatus, MovedFromPlacementId = movedFromPlacementId,
            CreatedByUserId = 3, UpdatedByUserId = 3,
        };
        await placement.Create(Db);
        return placement;
    }

    protected async Task ClosePlacementAsync(ChemicalPlacement placement, ChemicalRemovalReasonEnum reason)
    {
        placement.RemovedAt = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        placement.RemovedByUserId = 3;
        placement.RemovalReason = reason;
        await placement.Update(Db);
    }

    /// <summary>A property worker (SDK site id) with chemical flags on the property.</summary>
    protected async Task AddWorkerAsync(
        int propertyId, int siteId, bool admin = false, bool remove = false, bool view = true, bool stock = false,
        bool removedFromProperty = false)
    {
        var worker = new PropertyWorker { PropertyId = propertyId, WorkerId = siteId, CreatedByUserId = 1, UpdatedByUserId = 1 };
        await worker.Create(Db);
        if (removedFromProperty)
        {
            await worker.Delete(Db);
        }

        await new ChemicalWorkerPermission
        {
            PropertyId = propertyId, WorkerId = siteId, View = view, Remove = remove, Stock = stock, Admin = admin,
            CreatedByUserId = 1, UpdatedByUserId = 1,
        }.Create(Db);
    }

    protected async Task<DeviceToken> AddTokenAsync(int siteId, string fcmToken, string appId = "chemistry")
    {
        var token = new DeviceToken
        {
            AppId = appId, InstallationId = $"install-{fcmToken}", FcmToken = fcmToken, SdkSiteId = siteId, Platform = "android",
        };
        await token.Create(Db);
        return token;
    }

    protected async Task SetPluginValueAsync(string name, string value)
    {
        Db.PluginConfigurationValues.Add(new PluginConfigurationValue { Name = name, Value = value });
        await Db.SaveChangesAsync();
    }

    protected async Task SetDigestRecipientsAsync(int propertyId, string recipients)
    {
        await new ChemicalPropertySettings
        {
            PropertyId = propertyId, DigestRecipients = recipients, CreatedByUserId = 1, UpdatedByUserId = 1,
        }.Create(Db);
    }

    protected Task<int> AddChemicalAsync(string name, int status = 5, DateTime? sales = null, DateTime? possession = null) =>
        ChemicalJobSeed.AddChemicalAsync(_chemicals, name, status, sales, possession);
}
