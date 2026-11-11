namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;
using ChemicalsBase.Infrastructure.Data.Enums;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

[TestFixture]
public class ChemicalSdsLanguageSyncTests : ChemicalDocumentSyncTestBase
{
    protected override string DatabaseName => "chemical-sds-sync";

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Test]
    public async Task Per_language_sds_created_locally()
    {
        var id = await SeedChemicalAsync();
        var feed = Sds(SdsJson("da", Md5A, "2026-02-01T00:00:00Z", "Roundup 5 L"), SdsJson("en", Md5B));

        var changed = await ChemicalSdsLanguageSync.ApplyAsync(Db, id, feed, Ct);

        Assert.That(changed, Is.True);
        var docs = await DocsAsync(id);
        Assert.That(docs, Has.Count.EqualTo(2));
        var da = docs.Single(d => d.Language == "da");
        Assert.That(da.Type, Is.EqualTo(ChemicalDocumentType.Sds));
        Assert.That(da.Source, Is.EqualTo(ChemicalDocumentSource.Supplier));
        Assert.That(da.Status, Is.EqualTo(ChemicalDocumentStatus.Active));
        Assert.That(da.ContentMd5, Is.EqualTo(Md5A));
        Assert.That(da.SourceRevisionDate, Is.EqualTo(new DateTime(2026, 2, 1)));
        Assert.That(docs.Single(d => d.Language == "en").ContentMd5, Is.EqualTo(Md5B));
    }

    [Test]
    public async Task Replaced_language_revision_replaces_local_row()
    {
        var id = await SeedChemicalAsync();
        await ChemicalSdsLanguageSync.ApplyAsync(Db, id, Sds(SdsJson("en", Md5A), SdsJson("da", Md5C)), Ct);
        await AgeChemicalAsync(id);
        var before = await UpdatedAtAsync(id);
        await using var fresh = Helper.GetDbContext();

        var changed = await ChemicalSdsLanguageSync.ApplyAsync(fresh, id,
            Sds(SdsJson("en", Md5B, "2026-05-05T00:00:00Z"), SdsJson("da", Md5C)), Ct);

        Assert.That(changed, Is.True);
        var docs = await DocsAsync(id);
        Assert.That(docs.Single(d => d.ContentMd5 == Md5A).WorkflowState, Is.EqualTo(Removed));
        Assert.That(docs.Single(d => d.ContentMd5 == Md5B).WorkflowState, Is.EqualTo(Created));
        Assert.That(docs.Single(d => d.ContentMd5 == Md5C).WorkflowState, Is.EqualTo(Created), "the other language is untouched");
        Assert.That(await UpdatedAtAsync(id), Is.GreaterThan(before), "one bump for the replacement");
    }

    [Test]
    public async Task Sds_sync_never_touches_label_rows()
    {
        var id = await SeedChemicalAsync();
        var label = await InsertDocAsync(id, ChemicalDocumentType.Label, Md5A, "da", ChemicalDocumentSource.Bmd);
        var minorUse = await InsertDocAsync(id, ChemicalDocumentType.MinorUseInstructions, Md5B, "da", ChemicalDocumentSource.Bmd);
        await ChemicalSdsLanguageSync.ApplyAsync(Db, id, Sds(SdsJson("da", Md5C)), Ct);
        await using var fresh = Helper.GetDbContext();

        await ChemicalSdsLanguageSync.ApplyAsync(fresh, id, Sds(), Ct);

        var docs = await DocsAsync(id);
        Assert.That(docs.Single(d => d.Type == ChemicalDocumentType.Sds).WorkflowState, Is.EqualTo(Removed));
        foreach (var kept in new[] { label, minorUse })
        {
            var after = docs.Single(d => d.Id == kept.Id);
            Assert.That(after.WorkflowState, Is.EqualTo(Created), "an empty sds list must not soft-delete label rows (F48)");
            Assert.That(after.Version, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Null_sds_documents_change_nothing()
    {
        var id = await SeedChemicalAsync();
        await ChemicalSdsLanguageSync.ApplyAsync(Db, id, Sds(SdsJson("da", Md5A)), Ct);
        await AgeChemicalAsync(id);
        var before = await UpdatedAtAsync(id);
        await using var fresh = Helper.GetDbContext();

        var changed = await ChemicalSdsLanguageSync.ApplyAsync(fresh, id, null, Ct);

        Assert.That(changed, Is.False);
        Assert.That((await DocsAsync(id)).Single().WorkflowState, Is.EqualTo(Created), "an old feed deletes nothing");
        Assert.That(await UpdatedAtAsync(id), Is.EqualTo(before));
    }

    [Test]
    public async Task Products_filename_matching_unchanged()
    {
        const string products = "[{\"barcode\":\"5700000000024\",\"name\":\"Roundup 1 L\",\"fileName\":\"" + Md5C
                                + "\",\"checksum\":\"sum\",\"verified\":true}]";
        var remoteId = Guid.NewGuid().ToString("N");
        var sds = "[" + SdsJson("da", Md5C) + "," + SdsJson("en", Md5B) + "]";

        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(remoteId, productsJson: products, sdsJson: sds), Ct);
        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(remoteId, productsJson: products, sdsJson: sds), Ct);

        await using var check = Helper.GetDbContext();
        var id = await check.Chemicals.Where(c => c.RemoteId == remoteId).Select(c => c.Id).SingleAsync();
        var product = await check.Products.AsNoTracking().SingleAsync(p => p.ChemicalId == id);
        Assert.That(product.FileName, Is.EqualTo(Md5C), "Products.FileName still comes from the product matcher");
        Assert.That(product.Name, Is.EqualTo("Roundup 1 L"));
        Assert.That((await DocsAsync(id)).Select(d => d.Language), Is.EquivalentTo(new[] { "da", "en" }));
    }

    [Test]
    public async Task Missing_documents_table_skips_sds_documents_only()
    {
        const string database = "chemical-sds-sync-no-table";
        ChemicalDocsDb.Create(database);
        try
        {
            await using (var setup = ChemicalDocsDb.Open(database))
            {
                await setup.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 0");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS `ChemicalDocumentVersions`");
                await setup.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS `ChemicalDocuments`");
            }

            var helper = ChemicalDocsDb.Helper(database);
            var remoteId = Guid.NewGuid().ToString("N");
            const string products = "[{\"barcode\":\"5700000000031\",\"name\":\"Roundup\",\"fileName\":\"" + Md5C
                                    + "\",\"checksum\":\"sum\",\"verified\":true}]";
            var sds = "[" + SdsJson("da", Md5C) + "]";
            ChemicalFeedDocuments.ResetRunLatch();

            await Assert.DoesNotThrowAsync(() => ChemicalRegisterUpsert.UpsertAsync(helper,
                FeedChemical(remoteId, productsJson: products, sdsJson: sds), Ct).AsTask());
            await Assert.DoesNotThrowAsync(() => ChemicalRegisterUpsert.UpsertAsync(helper,
                FeedChemical(remoteId, productsJson: products, sdsJson: sds), Ct).AsTask());

            Assert.That(ChemicalFeedDocuments.MissingTableLogCount, Is.EqualTo(1), "logged once per run, not per chemical");
            await using var check = ChemicalDocsDb.Open(database);
            var chemical = await check.Chemicals.Include(c => c.Products).SingleAsync(c => c.RemoteId == remoteId);
            Assert.That(chemical.Products.Single().FileName, Is.EqualTo(Md5C), "chemicals and products still sync");
        }
        finally
        {
            ChemicalDocsDb.Drop(database);
        }
    }
}
