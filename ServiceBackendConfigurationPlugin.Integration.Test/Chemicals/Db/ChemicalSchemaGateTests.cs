namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>
/// The tenant service never migrates its chemical DB; the plugin does. Until migration
/// 20261027063955_AddChemicalDocuments is in the tenant's __EFMigrationsHistory every chemical job skips that tenant.
/// The shared chemicals DB of ChemicalJobDatabases is used: SetUp removes the history row, TearDown restores it.
/// </summary>
[TestFixture]
public class ChemicalSchemaGateTests : ChemicalJobTestBase
{
    private static readonly DateTimeOffset AlertRunTime = new(2026, 10, 1, 5, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DigestRunTime = new(2026, 10, 26, 6, 10, 0, TimeSpan.Zero);

    private FakeChemicalPushSender _push;
    private FakeChemicalEmailSender _email;
    private FakeChemicalSdkDirectory _sdk;

    [SetUp]
    public void StartNotMigrated()
    {
        _push = new FakeChemicalPushSender();
        _email = new FakeChemicalEmailSender();
        _sdk = new FakeChemicalSdkDirectory { CustomerNo = "4711" };
        using var chemicals = ChemicalHelper.GetDbContext();
        ChemicalDocsDb.MarkNotMigrated(chemicals);
        ChemicalSchemaGate.ResetCache();
    }

    [TearDown]
    public void RestoreMigrated()
    {
        using var chemicals = ChemicalHelper.GetDbContext();
        ChemicalDocsDb.MarkMigrated(chemicals);
        ChemicalSchemaGate.ResetCache();
    }

    private async Task<int> ArrangeAlertAsync()
    {
        var property = await CreatePropertyAsync("Gården");
        var location = await CreateLocationAsync(property.Id);
        var chemicalId = await AddChemicalAsync("Roundup Flex", possession: new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Utc));
        await CreatePlacementAsync(location.Id, chemicalId, 5);
        await AddWorkerAsync(property.Id, 501, admin: true);
        await AddTokenAsync(501, "admin-phone");
        await SetPluginValueAsync(ChemicalAlertJob.FeatureFlagKey, "true");
        await SetPluginValueAsync(ChemicalAlertJob.ServiceAccountJsonKey, "{\"type\":\"service_account\"}");
        return property.Id;
    }

    private async Task ArrangeDigestAsync()
    {
        var property = await CreatePropertyAsync("Gården");
        var location = await CreateLocationAsync(property.Id);
        var chemicalId = await AddChemicalAsync("Roundup Flex", possession: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        await CreatePlacementAsync(location.Id, chemicalId, registeredAt: new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        await SetDigestRecipientsAsync(property.Id, "a@example.com");
    }

    private ChemicalAlertJob AlertJob() => new(DbHelper, ChemicalHelper, _sdk, _push, new FixedTimeProvider(AlertRunTime));

    private ChemicalDigestJob DigestJob() => new(DbHelper, ChemicalHelper, _sdk, _email, new FixedTimeProvider(DigestRunTime));

    [Test]
    public async Task Chemical_jobs_skip_until_migration_applied()
    {
        await ArrangeAlertAsync();
        await ArrangeDigestAsync();

        await Assert.DoesNotThrowAsync(() => AlertJob().RunAsync());
        await Assert.DoesNotThrowAsync(() => DigestJob().RunAsync());
        var facts = await ChemicalRegisterFactsReader.LoadAsync(ChemicalHelper, [1]);

        Assert.That(_push.Sent, Is.Empty, "the alert job skips a tenant whose chemical schema is not migrated");
        Assert.That(_email.Attempted, Is.Empty, "the digest job skips it too");
        Assert.That(facts, Is.Null, "and the register reader reports 'not migrated' instead of reading");
        await using var db = FreshDb();
        Assert.That(await db.ChemicalAlertLogs.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Jobs_run_after_migration_applied()
    {
        await ArrangeAlertAsync();
        await ArrangeDigestAsync();
        await using (var chemicals = ChemicalHelper.GetDbContext())
        {
            ChemicalDocsDb.MarkMigrated(chemicals);
        }

        await AlertJob().RunAsync();
        await DigestJob().RunAsync();

        Assert.That(_push.Sent.Select(s => s.Message.Token), Is.EqualTo(new[] { "admin-phone" }));
        Assert.That(_email.Attempted, Is.Not.Empty);
    }

    [Test]
    public async Task Not_migrated_error_is_skipped_only_when_the_chemical_gate_says_not_migrated()
    {
        const string database = "chemical-gate-skip-decision";
        ChemicalDocsDb.Create(database);
        try
        {
            await using var db = ChemicalDocsDb.Open(database);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Chemicals` DROP COLUMN `AdditionalTradeNames`");
            var error = await Assert.CatchAsync<Exception>(async () => await db.Chemicals.AsNoTracking().ToListAsync());
            var helper = ChemicalDocsDb.Helper(database);
            ChemicalSchemaGate.ResetCache();

            Assert.That(await ChemicalSchemaGate.ShouldSkipTenantAsync(helper, error), Is.False,
                "the chemical DB is migrated, so a 1054 is another database's problem and must reach Sentry");

            ChemicalDocsDb.MarkNotMigrated(db);
            ChemicalSchemaGate.ResetCache();
            Assert.That(await ChemicalSchemaGate.ShouldSkipTenantAsync(helper, error), Is.True);
            Assert.That(await ChemicalSchemaGate.ShouldSkipTenantAsync(helper, new InvalidOperationException("boom")), Is.False);
        }
        finally
        {
            ChemicalDocsDb.Drop(database);
        }
    }

    [Test]
    public async Task Missing_column_on_a_migrated_looking_tenant_hits_1054_in_the_upsert_and_is_not_skipped()
    {
        // The jobs' reads project columns and never select AdditionalTradeNames; the nightly upsert reads the whole
        // Chemicals entity, so that is the real path on which a half-migrated tenant meets MySQL 1054.
        const string database = "chemical-gate-upsert-1054";
        await ChemicalDocsDb.UseTemporaryDatabaseAsync(database, async () =>
        {
            await using (var db = ChemicalDocsDb.Open(database))
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Chemicals` DROP COLUMN `AdditionalTradeNames`");
            }

            ChemicalSchemaGate.ResetCache();
            var helper = ChemicalDocsDb.Helper(database);
            var error = await Assert.CatchAsync<Exception>(async () => await ChemicalRegisterUpsert.UpsertAsync(
                helper, new ChemicalsBase.Infrastructure.Data.Entities.Chemical { RemoteId = "x" }, CancellationToken.None));

            Assert.That(ChemicalSchemaGate.IsNotMigratedError(error), Is.True, "a real 1054 from the real upsert path");
            Assert.That(await ChemicalSchemaGate.ShouldSkipTenantAsync(helper, error), Is.False,
                "the migration row is present, so the job's catch reports it to Sentry instead of skipping");
        });
    }

    [Test]
    public async Task Gate_check_failure_warns_sentry_once_per_tenant_not_per_call()
    {
        ChemicalSchemaGate.ResetCache();
        await using var db = ChemicalDocsDb.Open("chemical-gate-database-that-does-not-exist");

        for (var tick = 0; tick < 3; tick++)
        {
            Assert.That(await ChemicalSchemaGate.IsMigratedAsync(db, CancellationToken.None), Is.False);
        }

        Assert.That(ChemicalSchemaGate.FailureWarningCount, Is.EqualTo(1), "one warning per tenant, not one per tick");
    }

    [Test]
    public async Task Missing_column_1054_is_treated_as_not_migrated()
    {
        const string database = "chemical-gate-no-column";
        ChemicalDocsDb.Create(database);
        try
        {
            await using var db = ChemicalDocsDb.Open(database);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Chemicals` DROP COLUMN `AdditionalTradeNames`");

            var error = await Assert.CatchAsync<Exception>(async () => await db.Chemicals.AsNoTracking().ToListAsync(),
                "the model maps the column, the database lacks it: MySQL error 1054");

            Assert.That(ChemicalSchemaGate.IsNotMigratedError(error), Is.True);
            Assert.That(ChemicalSchemaGate.IsNotMigratedError(new InvalidOperationException("boom")), Is.False);
        }
        finally
        {
            ChemicalDocsDb.Drop(database);
        }
    }

    [Test]
    public async Task Missing_table_1146_is_treated_as_not_migrated()
    {
        const string database = "chemical-gate-no-table";
        ChemicalDocsDb.Create(database);
        try
        {
            await ChemicalDocsDb.DropDocumentTablesAsync(database);
            await using (var setup = ChemicalDocsDb.Open(database))
            {
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS `__EFMigrationsHistory`");
            }

            await using var db = ChemicalDocsDb.Open(database);
            var error = await Assert.CatchAsync<Exception>(async () => await db.ChemicalDocuments.AsNoTracking().ToListAsync(),
                "MySQL error 1146");

            Assert.That(ChemicalSchemaGate.IsNotMigratedError(error), Is.True);
            ChemicalSchemaGate.ResetCache();
            Assert.That(await ChemicalSchemaGate.IsMigratedAsync(db, CancellationToken.None), Is.False,
                "a missing __EFMigrationsHistory table (1146) means not migrated; the gate never throws");
        }
        finally
        {
            ChemicalDocsDb.Drop(database);
        }
    }
}
