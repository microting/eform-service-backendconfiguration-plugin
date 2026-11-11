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
public class ChemicalLabelSyncTests : ChemicalDocumentSyncTestBase
{
    protected override string DatabaseName => "chemical-label-sync";

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Test]
    public async Task Feed_labels_are_created_locally()
    {
        var id = await SeedChemicalAsync();
        var labels = Labels(
            LabelJson(ChemicalDocumentType.Label, Md5A, "5 L", "2026-03-01T00:00:00Z", "3"),
            LabelJson(ChemicalDocumentType.MinorUseInstructions, Md5B));

        var changed = await ChemicalLabelSync.ApplyAsync(Db, id, labels, Ct);

        Assert.That(changed, Is.True);
        var docs = await DocsAsync(id);
        Assert.That(docs, Has.Count.EqualTo(2));
        var label = docs.Single(d => d.Type == ChemicalDocumentType.Label);
        Assert.That(label.ContentMd5, Is.EqualTo(Md5A));
        Assert.That(label.Source, Is.EqualTo(ChemicalDocumentSource.Bmd));
        Assert.That(label.Status, Is.EqualTo(ChemicalDocumentStatus.Active));
        Assert.That(label.Language, Is.EqualTo("da"));
        Assert.That(label.SourcePackaging, Is.EqualTo("5 L"));
        Assert.That(label.SourceRevision, Is.EqualTo("3"));
        Assert.That(label.SourceRevisionDate, Is.EqualTo(new DateTime(2026, 3, 1)));
        Assert.That(label.WorkflowState, Is.EqualTo(Created));
        var minorUse = docs.Single(d => d.Type == ChemicalDocumentType.MinorUseInstructions);
        Assert.That(minorUse.ContentMd5, Is.EqualTo(Md5B));
        Assert.That(minorUse.Source, Is.EqualTo(ChemicalDocumentSource.Bmd));
    }

    [Test]
    public async Task Labels_applied_for_new_chemical_too()
    {
        var remoteId = Guid.NewGuid().ToString("N");
        var feed = FeedChemical(remoteId, labelsJson: "[" + LabelJson(ChemicalDocumentType.Label, Md5A, "1 L") + "]");

        await ChemicalRegisterUpsert.UpsertAsync(Helper, feed, Ct);

        await using var check = Helper.GetDbContext();
        var id = await check.Chemicals.Where(c => c.RemoteId == remoteId).Select(c => c.Id).SingleAsync();
        var doc = (await DocsAsync(id)).Single();
        Assert.That(doc.Type, Is.EqualTo(ChemicalDocumentType.Label));
        Assert.That(doc.ContentMd5, Is.EqualTo(Md5A));
        Assert.That(doc.SourcePackaging, Is.EqualTo("1 L"));
    }

    [Test]
    public async Task Label_replaced_centrally_replaces_local_row_and_bumps_chemical()
    {
        var id = await SeedChemicalAsync();
        await ChemicalLabelSync.ApplyAsync(Db, id, Labels(LabelJson(ChemicalDocumentType.Label, Md5A)), Ct);
        await AgeChemicalAsync(id);
        var before = await UpdatedAtAsync(id);
        await using var fresh = Helper.GetDbContext();

        var changed = await ChemicalLabelSync.ApplyAsync(fresh, id, Labels(LabelJson(ChemicalDocumentType.Label, Md5B)), Ct);

        Assert.That(changed, Is.True);
        var docs = await DocsAsync(id);
        Assert.That(docs.Single(d => d.ContentMd5 == Md5A).WorkflowState, Is.EqualTo(Removed));
        Assert.That(docs.Single(d => d.ContentMd5 == Md5B).WorkflowState, Is.EqualTo(Created));
        Assert.That(await UpdatedAtAsync(id), Is.GreaterThan(before), "the chemical is bumped so the app re-syncs it");
    }

    [Test]
    public async Task Soft_deleted_label_reappears_is_restored()
    {
        var id = await SeedChemicalAsync();
        var label = Labels(LabelJson(ChemicalDocumentType.Label, Md5A, "5 L"));
        await ChemicalLabelSync.ApplyAsync(Db, id, label, Ct);
        await ChemicalLabelSync.ApplyAsync(Db, id, Labels(), Ct);
        Assert.That((await DocsAsync(id)).Single().WorkflowState, Is.EqualTo(Removed));
        await using var fresh = Helper.GetDbContext();

        var changed = await ChemicalLabelSync.ApplyAsync(fresh, id, label, Ct);

        Assert.That(changed, Is.True);
        var docs = await DocsAsync(id);
        Assert.That(docs, Has.Count.EqualTo(1), "revived through Update, never re-created (unique index)");
        Assert.That(docs[0].WorkflowState, Is.EqualTo(Created));
        Assert.That(docs[0].Status, Is.EqualTo(ChemicalDocumentStatus.Active));
    }

    [Test]
    public async Task Label_sync_never_touches_sds_rows()
    {
        var id = await SeedChemicalAsync();
        var sds = await InsertDocAsync(id, ChemicalDocumentType.Sds, Md5C, "da", ChemicalDocumentSource.Supplier);
        await ChemicalLabelSync.ApplyAsync(Db, id, Labels(LabelJson(ChemicalDocumentType.Label, Md5A)), Ct);
        await using var fresh = Helper.GetDbContext();

        await ChemicalLabelSync.ApplyAsync(fresh, id, Labels(), Ct);

        var docs = await DocsAsync(id);
        Assert.That(docs.Single(d => d.Type == ChemicalDocumentType.Label).WorkflowState, Is.EqualTo(Removed));
        var sdsAfter = docs.Single(d => d.Type == ChemicalDocumentType.Sds);
        Assert.That(sdsAfter.Id, Is.EqualTo(sds.Id));
        Assert.That(sdsAfter.WorkflowState, Is.EqualTo(Created), "an empty label list must not soft-delete SDS rows (F48)");
        Assert.That(sdsAfter.Version, Is.EqualTo(1));
    }

    [Test]
    public async Task Null_labels_change_nothing()
    {
        var id = await SeedChemicalAsync();
        await ChemicalLabelSync.ApplyAsync(Db, id, Labels(LabelJson(ChemicalDocumentType.Label, Md5A)), Ct);
        await AgeChemicalAsync(id);
        var before = await UpdatedAtAsync(id);
        await using var fresh = Helper.GetDbContext();

        var changed = await ChemicalLabelSync.ApplyAsync(fresh, id, null, Ct);

        Assert.That(changed, Is.False);
        var doc = (await DocsAsync(id)).Single();
        Assert.That(doc.WorkflowState, Is.EqualTo(Created), "an old feed (labels absent) deletes nothing");
        Assert.That(await UpdatedAtAsync(id), Is.EqualTo(before), "and does not bump the chemical");
    }

    private static async Task<List<Product>> ProductsOfAsync(ChemicalsDbContext check, string remoteId)
    {
        var id = await check.Chemicals.Where(c => c.RemoteId == remoteId).Select(c => c.Id).SingleAsync();
        return await check.Products.AsNoTracking().Where(p => p.ChemicalId == id).ToListAsync();
    }

    [Test]
    public async Task Products_matching_unchanged_by_labels()
    {
        const string products = "[{\"barcode\":\"5700000000017\",\"name\":\"Roundup 5 L\",\"fileName\":\"" + Md5C
                                + "\",\"checksum\":\"sum\",\"verified\":true}]";
        var withLabels = Guid.NewGuid().ToString("N");
        var without = Guid.NewGuid().ToString("N");
        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(without, productsJson: products), Ct);
        await ChemicalRegisterUpsert.UpsertAsync(Helper,
            FeedChemical(withLabels, productsJson: products,
                labelsJson: "[" + LabelJson(ChemicalDocumentType.Label, Md5A) + "]"), Ct);

        // Second night: both existing chemicals take the matcher path again.
        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(without, productsJson: products), Ct);
        await ChemicalRegisterUpsert.UpsertAsync(Helper,
            FeedChemical(withLabels, productsJson: products,
                labelsJson: "[" + LabelJson(ChemicalDocumentType.Label, Md5A) + "]"), Ct);

        await using var check = Helper.GetDbContext();
        var a = await ProductsOfAsync(check, without);
        var b = await ProductsOfAsync(check, withLabels);
        Assert.That(b.Select(p => (p.Barcode, p.Name, p.FileName)),
            Is.EqualTo(a.Select(p => (p.Barcode, p.Name, p.FileName))));
        Assert.That(b.Single().FileName, Is.EqualTo(Md5C), "Products.FileName still comes from the product matcher");
    }

    [Test]
    public async Task Missing_documents_table_skips_labels_only()
    {
        const string database = "chemical-label-sync-no-table";
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
            const string products = "[{\"barcode\":\"5700000000017\",\"name\":\"Roundup\",\"fileName\":\"" + Md5C
                                    + "\",\"checksum\":\"sum\",\"verified\":true}]";
            ChemicalFeedDocuments.ResetRunLatch();

            await Assert.DoesNotThrowAsync(() => ChemicalRegisterUpsert.UpsertAsync(helper, FeedChemical(remoteId,
                productsJson: products, labelsJson: "[" + LabelJson(ChemicalDocumentType.Label, Md5A) + "]"), Ct).AsTask(),
                "the chemical is created even though the documents table is missing");
            await Assert.DoesNotThrowAsync(() => ChemicalRegisterUpsert.UpsertAsync(helper, FeedChemical(remoteId,
                productsJson: products, labelsJson: "[" + LabelJson(ChemicalDocumentType.Label, Md5A) + "]"), Ct).AsTask(),
                "and an existing chemical is updated");

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
