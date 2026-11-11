/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Sentry;
using MySqlConnector;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// The tenant service never migrates its chemical database; the web plugin does (Database.Migrate at plugin start).
/// ChemicalsBase maps the column Chemicals.AdditionalTradeNames and the ChemicalDocuments tables, so until migration
/// <see cref="RequiredMigration"/> is applied every chemical job must skip that tenant instead of failing.
/// </summary>
public static class ChemicalSchemaGate
{
    public const string RequiredMigration = "20261027063955_AddChemicalDocuments";

    private const int UnknownColumn = 1054;
    private const int UnknownTable = 1146;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    // Only "migrated" is cached: a migration is never rolled back, while "not yet" must be re-checked on the next run.
    private static readonly ConcurrentDictionary<string, bool> Migrated = new();

    /// <summary>
    /// True when the migration is in this tenant's __EFMigrationsHistory. One bounded query (5 s); a positive answer is
    /// cached per tenant. Never throws: a failing or slow check counts as not migrated, so the caller skips the tenant.
    /// </summary>
    public static async Task<bool> IsMigratedAsync(ChemicalsDbContext db, CancellationToken ct = default)
    {
        var tenant = db.Database.GetConnectionString() ?? "";
        if (Migrated.ContainsKey(tenant))
        {
            return true;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(CheckTimeout);
        try
        {
            var applied = (await db.Database
                .SqlQuery<long>($"SELECT CAST(COUNT(*) AS SIGNED) AS `Value` FROM `__EFMigrationsHistory` WHERE `MigrationId` = {RequiredMigration}")
                .ToListAsync(bounded.Token).ConfigureAwait(false)).Single();
            if (applied > 0)
            {
                Migrated[tenant] = true;
            }

            return applied > 0;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"info: chemical schema check failed, treating the tenant as not migrated: {e.GetType().Name}: {e.Message}");
            if (!IsMissingTable(e))
            {
                // A missing history table just means "not migrated"; anything else would switch the chemical jobs off
                // for this tenant silently, so make it visible.
                SentrySdk.CaptureMessage($"ChemicalSchemaGate: the migration check failed ({e.GetType().Name}: {e.Message}); chemical jobs skip this tenant",
                    scope => scope.SetFingerprint(["chemical-schema-gate-check-failed"]), SentryLevel.Warning);
            }

            return false;
        }
    }

    /// <summary>MySQL 1054 (unknown column) or 1146 (unknown table), anywhere in the exception chain.</summary>
    public static bool IsNotMigratedError(Exception e) =>
        IsMissingTable(e) || Chain(e).OfType<MySqlException>().Any(x => x.Number == UnknownColumn);

    /// <summary>
    /// For a job's catch: true only when <paramref name="e"/> looks like a missing chemical column or table AND the
    /// chemical database really is not migrated. A 1054/1146 from another database (e.g. backend-configuration) is not
    /// this tenant's chemical migration problem and must reach Sentry.
    /// </summary>
    public static async Task<bool> ShouldSkipTenantAsync(ChemicalDbContextHelper helper, Exception e)
    {
        if (!IsNotMigratedError(e))
        {
            return false;
        }

        await using var db = helper.GetDbContext();
        return !await IsMigratedAsync(db).ConfigureAwait(false);
    }

    /// <summary>MySQL 1146: the table does not exist.</summary>
    public static bool IsMissingTable(Exception e) =>
        Chain(e).OfType<MySqlException>().Any(x => x.Number == UnknownTable);

    /// <summary>Forgets the cached answers (tests).</summary>
    public static void ResetCache() => Migrated.Clear();

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
