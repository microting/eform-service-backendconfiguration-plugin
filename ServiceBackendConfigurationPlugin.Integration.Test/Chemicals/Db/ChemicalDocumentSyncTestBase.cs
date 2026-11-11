#nullable enable

namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;
using ChemicalsBase.Infrastructure.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Real MariaDB (the CI service on localhost:3306). Each fixture owns one private chemical database;
/// every test seeds its own chemicals and reads back only their rows.
/// </summary>
[NonParallelizable]
public abstract class ChemicalDocumentSyncTestBase
{
    protected const string Md5A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    protected const string Md5B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    protected const string Md5C = "cccccccccccccccccccccccccccccccc";

    protected abstract string DatabaseName { get; }

    protected ChemicalDbContextHelper Helper => ChemicalDocsDb.Helper(DatabaseName);

    protected ChemicalsDbContext Db { get; private set; }

    [OneTimeSetUp]
    public void CreateDatabase() => ChemicalDocsDb.Create(DatabaseName);

    [OneTimeTearDown]
    public void DropDatabase() => ChemicalDocsDb.Drop(DatabaseName);

    [SetUp]
    public void OpenContext() => Db = Helper.GetDbContext();

    [TearDown]
    public async Task CloseContext() => await Db.DisposeAsync();

    protected Task<int> SeedChemicalAsync(string name = "Roundup Flex") =>
        ChemicalJobSeed.AddChemicalAsync(Db, name);

    protected static async Task<List<ChemicalDocument>> DocsAsync(ChemicalDbContextHelper helper, int chemicalId)
    {
        await using var db = helper.GetDbContext();
        return await db.ChemicalDocuments.AsNoTracking().Where(d => d.ChemicalId == chemicalId)
            .OrderBy(d => d.Id).ToListAsync();
    }

    protected Task<List<ChemicalDocument>> DocsAsync(int chemicalId) => DocsAsync(Helper, chemicalId);

    protected async Task<DateTime> UpdatedAtAsync(int chemicalId)
    {
        await using var db = Helper.GetDbContext();
        return await db.Chemicals.AsNoTracking().Where(c => c.Id == chemicalId).Select(c => c.UpdatedAt).SingleAsync();
    }

    /// <summary>Moves the chemical's UpdatedAt into the past so a later bump is observable without sleeping.</summary>
    protected async Task AgeChemicalAsync(int chemicalId)
    {
        await using var db = Helper.GetDbContext();
        await db.Chemicals.Where(c => c.Id == chemicalId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>A ChemicalDocument row as a tenant would hold it, created directly (not through the sync).</summary>
    protected async Task<ChemicalDocument> InsertDocAsync(
        int chemicalId, ChemicalDocumentType type, string md5, string? language, ChemicalDocumentSource source)
    {
        var doc = new ChemicalDocument
        {
            ChemicalId = chemicalId, Type = type, Source = source, Status = ChemicalDocumentStatus.Active,
            Language = language, ContentMd5 = md5, RetrievedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        };
        await doc.Create(Db);
        return doc;
    }

    /// <summary>
    /// A feed chemical deserialized from JSON in the shape of the chemical plugin's /get-all-chemicals
    /// (camelCase, enums as numbers, the arrays absent on an old feed), with the guard's own options.
    /// </summary>
    protected static Chemical FeedChemical(string remoteId, string? labelsJson = null, string? sdsJson = null,
        string productsJson = "[]")
    {
        var json = "{\"remoteId\":\"" + remoteId + "\",\"name\":\"Roundup Flex\",\"registrationNo\":\"1-2\","
                   + "\"status\":5,\"authorisationHolder\":{\"name\":\"Holder A/S\",\"address\":{\"country\":45},"
                   + "\"remoteId\":\"holder-" + remoteId + "\"},"
                   + "\"classificationAndLabeling\":{\"clp\":{\"hazardPictograms\":[7],\"signalWord\":1,\"hazardStatements\":[]},"
                   + "\"dpd\":{\"riskPhrases\":[]}},"
                   + "\"barcodeValue\":[],\"biocideUser\":[],\"pesticideProductGroup\":[],\"biocideProductType\":[],"
                   + "\"pesticidePossibleUse\":[],\"biocidePossibleUse\":[],\"biocideSpecialUse\":[],\"activeSubstances\":[],"
                   + "\"products\":" + productsJson
                   + (labelsJson == null ? "" : ",\"labels\":" + labelsJson)
                   + (sdsJson == null ? "" : ",\"sdsDocuments\":" + sdsJson) + "}";
        return JsonSerializer.Deserialize<Chemical>(json, ChemicalFeedGuard.FeedJsonOptions)!;
    }

    protected static string LabelJson(ChemicalDocumentType type, string fileName, string? packaging = null,
        string? revisionDate = null, string? revision = null) =>
        "{\"type\":" + (int)type + ",\"fileName\":\"" + fileName + "\","
        + "\"packaging\":" + (packaging == null ? "null" : "\"" + packaging + "\"") + ","
        + "\"revisionDate\":" + (revisionDate == null ? "null" : "\"" + revisionDate + "\"") + ","
        + "\"revision\":" + (revision == null ? "null" : "\"" + revision + "\"") + "}";

    protected static string SdsJson(string language, string fileName, string? revisionDate = null,
        string? productName = null) =>
        "{\"language\":\"" + language + "\",\"fileName\":\"" + fileName + "\","
        + "\"revisionDate\":" + (revisionDate == null ? "null" : "\"" + revisionDate + "\"") + ","
        + "\"productName\":" + (productName == null ? "null" : "\"" + productName + "\"") + "}";

    private static List<T> Feed<T>(string[] items) =>
        JsonSerializer.Deserialize<List<T>>("[" + string.Join(",", items) + "]", ChemicalFeedGuard.FeedJsonOptions)!;

    protected static List<ChemicalLabelFeedItem> Labels(params string[] items) => Feed<ChemicalLabelFeedItem>(items);

    protected static List<ChemicalSdsFeedItem> Sds(params string[] items) => Feed<ChemicalSdsFeedItem>(items);

    protected static string LabelsArray(params string[] items) => "[" + string.Join(",", items) + "]";

    protected static string ProductsJson(string barcode, string name) =>
        "[{\"barcode\":\"" + barcode + "\",\"name\":\"" + name + "\",\"fileName\":\"" + Md5C
        + "\",\"checksum\":\"sum\",\"verified\":true}]";

    /// <summary>
    /// The plugin has not migrated: the documents table is missing. Upserting a new and then an existing chemical must
    /// not throw, the chemical and its product still sync, and the skip is logged once for the whole run.
    /// </summary>
    protected static Task AssertMissingDocumentsTableSkipsDocumentsOnlyAsync(
        string database, string? labelsJson, string? sdsJson) =>
        ChemicalDocsDb.UseTemporaryDatabaseAsync(database, async () =>
        {
            await ChemicalDocsDb.DropDocumentTablesAsync(database);
            var helper = ChemicalDocsDb.Helper(database);
            var remoteId = Guid.NewGuid().ToString("N");
            ChemicalRegisterUpsert.ResetRunLatch();

            for (var pass = 0; pass < 2; pass++)
            {
                await Assert.DoesNotThrowAsync(() => ChemicalRegisterUpsert.UpsertAsync(helper,
                    FeedChemical(remoteId, labelsJson, sdsJson, ProductsJson("5700000000017", "Roundup")),
                    CancellationToken.None).AsTask());
            }

            Assert.That(ChemicalRegisterUpsert.MissingTableLogCount, Is.EqualTo(1), "logged once per run, not per chemical");
            await using var check = ChemicalDocsDb.Open(database);
            var chemical = await check.Chemicals.Include(c => c.Products).SingleAsync(c => c.RemoteId == remoteId);
            Assert.That(chemical.Products.Single().FileName, Is.EqualTo(Md5C), "chemicals and products still sync");
        });

    protected static string Created => Constants.WorkflowStates.Created;

    protected static string Removed => Constants.WorkflowStates.Removed;
}
