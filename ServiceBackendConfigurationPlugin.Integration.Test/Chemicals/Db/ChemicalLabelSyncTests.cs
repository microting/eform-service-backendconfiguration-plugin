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
        var feed = FeedChemical(remoteId, labelsJson: LabelsArray(LabelJson(ChemicalDocumentType.Label, Md5A, "1 L")));

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
        var products = ProductsJson("5700000000017", "Roundup 5 L");
        var labels = LabelsArray(LabelJson(ChemicalDocumentType.Label, Md5A));
        var withLabels = Guid.NewGuid().ToString("N");
        var without = Guid.NewGuid().ToString("N");

        // Two nights: the second takes the existing-chemical matcher path.
        for (var night = 0; night < 2; night++)
        {
            await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(without, productsJson: products), Ct);
            await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(withLabels, labels, productsJson: products), Ct);
        }

        await using var check = Helper.GetDbContext();
        var a = await ProductsOfAsync(check, without);
        var b = await ProductsOfAsync(check, withLabels);
        Assert.That(b.Select(p => (p.Barcode, p.Name, p.FileName)),
            Is.EqualTo(a.Select(p => (p.Barcode, p.Name, p.FileName))));
        Assert.That(b.Single().FileName, Is.EqualTo(Md5C), "Products.FileName still comes from the product matcher");
    }

    private async Task<int> ChemicalVersionAsync(int chemicalId)
    {
        await using var db = Helper.GetDbContext();
        return await db.Chemicals.AsNoTracking().Where(c => c.Id == chemicalId).Select(c => c.Version).SingleAsync();
    }

    private async Task<int> IdOfAsync(string remoteId)
    {
        await using var db = Helper.GetDbContext();
        return await db.Chemicals.Where(c => c.RemoteId == remoteId).Select(c => c.Id).SingleAsync();
    }

    [Test]
    public async Task Upsert_bumps_chemical_exactly_once_when_only_documents_change()
    {
        var remoteId = Guid.NewGuid().ToString("N");
        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(remoteId), Ct);
        var id = await IdOfAsync(remoteId);
        await AgeChemicalAsync(id);
        var before = await UpdatedAtAsync(id);
        var versionBefore = await ChemicalVersionAsync(id);

        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(remoteId,
            labelsJson: LabelsArray(LabelJson(ChemicalDocumentType.Label, Md5A)),
            sdsJson: "[" + SdsJson("da", Md5B) + "]"), Ct);

        Assert.That(await DocsAsync(id), Has.Count.EqualTo(2));
        Assert.That(await UpdatedAtAsync(id), Is.GreaterThan(before), "a document-only change re-sends the chemical (F5)");
        Assert.That(await ChemicalVersionAsync(id), Is.EqualTo(versionBefore + 1),
            "labels and SDS both changed, yet the chemical is bumped once");
    }

    [Test]
    public async Task Changed_packaging_or_revision_updates_row_in_place()
    {
        var id = await SeedChemicalAsync();
        await ChemicalLabelSync.ApplyAsync(Db, id,
            Labels(LabelJson(ChemicalDocumentType.Label, Md5A, "1 L", "2026-03-01T00:00:00", "1")), Ct);
        var original = (await DocsAsync(id)).Single();
        await AgeChemicalAsync(id);
        var before = await UpdatedAtAsync(id);
        await using var fresh = Helper.GetDbContext();

        var changed = await ChemicalLabelSync.ApplyAsync(fresh, id,
            Labels(LabelJson(ChemicalDocumentType.Label, Md5A, "5 L", "2026-04-02T00:00:00", "2")), Ct);

        Assert.That(changed, Is.True);
        var row = (await DocsAsync(id)).Single();
        Assert.That(row.Id, Is.EqualTo(original.Id));
        Assert.That(row.SourcePackaging, Is.EqualTo("5 L"));
        Assert.That(row.SourceRevision, Is.EqualTo("2"));
        Assert.That(row.SourceRevisionDate, Is.EqualTo(new DateTime(2026, 4, 2)));
        Assert.That(row.Version, Is.EqualTo(2));
        Assert.That(await UpdatedAtAsync(id), Is.GreaterThan(before));
    }

    [Test]
    public async Task Identical_feed_twice_changes_nothing()
    {
        var remoteId = Guid.NewGuid().ToString("N");
        var labels = "[" + LabelJson(ChemicalDocumentType.Label, Md5A, "1 L", "2026-03-01T10:15:30.123456", "1") + "]";
        var sds = "[" + SdsJson("en", Md5B, "2026-03-01T10:15:30.123456") + "]";
        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(remoteId, labels, sds), Ct);
        var id = await IdOfAsync(remoteId);
        await AgeChemicalAsync(id);
        var before = await UpdatedAtAsync(id);
        var versionBefore = await ChemicalVersionAsync(id);

        await ChemicalRegisterUpsert.UpsertAsync(Helper, FeedChemical(remoteId, labels, sds), Ct);

        var docs = await DocsAsync(id);
        Assert.That(docs, Has.Count.EqualTo(2), "the label and the SDS document exist");
        Assert.That(docs.Select(d => d.Version), Is.All.EqualTo(1), "no document is rewritten");
        Assert.That(await UpdatedAtAsync(id), Is.EqualTo(before), "and the chemical is not bumped");
        Assert.That(await ChemicalVersionAsync(id), Is.EqualTo(versionBefore));
    }

    [Test]
    public Task Document_changes_roll_back_when_the_chemical_bump_fails() =>
        ChemicalDocsDb.UseTemporaryDatabaseAsync("chemical-label-sync-bump-fails", async () =>
        {
            const string database = "chemical-label-sync-bump-fails";
            var helper = ChemicalDocsDb.Helper(database);
            var remoteId = Guid.NewGuid().ToString("N");
            await ChemicalRegisterUpsert.UpsertAsync(helper, FeedChemical(remoteId), Ct);
            await using (var setup = ChemicalDocsDb.Open(database))
            {
                // Any later write to Chemicals fails, as the bump would on a lost connection or a deadlock.
                await setup.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER `fail_chemical_update` BEFORE UPDATE ON `Chemicals` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'bump failed'");
            }

            await Assert.CatchAsync<Exception>(() => ChemicalRegisterUpsert.UpsertAsync(helper, FeedChemical(remoteId,
                labelsJson: LabelsArray(LabelJson(ChemicalDocumentType.Label, Md5A)),
                sdsJson: "[" + SdsJson("da", Md5B) + "]"), Ct).AsTask());

            await using var check = ChemicalDocsDb.Open(database);
            Assert.That(await check.ChemicalDocuments.CountAsync(), Is.Zero,
                "documents and the chemical bump are one unit: a failed bump leaves no document behind, so a retry repairs it");
        });

    [Test]
    public Task Missing_documents_table_skips_labels_only() =>
        AssertMissingDocumentsTableSkipsDocumentsOnlyAsync("chemical-label-sync-no-table",
            LabelsArray(LabelJson(ChemicalDocumentType.Label, Md5A)), sdsJson: null);
}
