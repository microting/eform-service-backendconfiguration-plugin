namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    protected static List<ChemicalLabelFeedItem> Labels(params string[] items) =>
        JsonSerializer.Deserialize<List<ChemicalLabelFeedItem>>("[" + string.Join(",", items) + "]",
            ChemicalFeedGuard.FeedJsonOptions)!;

    protected static List<ChemicalSdsFeedItem> Sds(params string[] items) =>
        JsonSerializer.Deserialize<List<ChemicalSdsFeedItem>>("[" + string.Join(",", items) + "]",
            ChemicalFeedGuard.FeedJsonOptions)!;

    protected static string Created => Constants.WorkflowStates.Created;

    protected static string Removed => Constants.WorkflowStates.Removed;
}
