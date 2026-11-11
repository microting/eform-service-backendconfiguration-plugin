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

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>The nightly register upsert of one feed chemical (extracted from SearchListJob so it can be tested).</summary>
public static class ChemicalRegisterUpsert
{
    private static int _missingTableLogged;

    /// <summary>How often the missing-table skip was logged since <see cref="ResetRunLatch"/> (once per run, not per chemical).</summary>
    public static int MissingTableLogCount => Volatile.Read(ref _missingTableLogged);

    /// <summary>Called at the start of each feed run so the missing-table line is logged once per run.</summary>
    public static void ResetRunLatch() => Volatile.Write(ref _missingTableLogged, 0);

    /// <summary>Upserts one feed chemical, matched on RemoteId (see ChemicalFeedGuard).</summary>
    public static async ValueTask UpsertAsync(ChemicalDbContextHelper chemicalDbContextHelper, Chemical chemical, CancellationToken ct)
    {
        await using var chemicalsDbContext = chemicalDbContextHelper.GetDbContext();
        var localRows = await chemicalsDbContext.Chemicals
            .Include(x => x.Products)
            .Where(x => x.RemoteId == chemical.RemoteId)
            .ToListAsync(ct).ConfigureAwait(false);
        var c = ChemicalFeedGuard.PickLocalMatch(localRows);
        if (c == null)
        {
            Console.WriteLine($"info: Chemical does not exist, so creating : {chemical.Name}");
            await chemical.Create(chemicalsDbContext).ConfigureAwait(false);
            await ApplyDocumentsAsync(chemicalsDbContext, chemical.Id, chemical, ct)
                .ConfigureAwait(false);
            return;
        }

        if (ChemicalFeedGuard.TryRestore(c))
        {
            Console.WriteLine($"info: Chemical was removed locally but is in the feed again, restoring : {chemical.Name}");
        }

        // Keys and names follow the feed, so a changed RegistrationNo never leaves a stale
        // local value behind. Blank feed values never overwrite local ones.
        ChemicalFeedGuard.CopyIdentity(c, chemical);

        c.Use = chemical.Use;
        c.Verified = chemical.Verified;
        c.AuthorisationDate = chemical.AuthorisationDate;
        c.AuthorisationExpirationDate = chemical.AuthorisationExpirationDate;
        c.AuthorisationTerminationDate = chemical.AuthorisationTerminationDate;
        c.UseAndPossesionDeadline = chemical.UseAndPossesionDeadline;
        c.PossessionDeadline = chemical.PossessionDeadline;
        c.SalesDeadline = chemical.SalesDeadline;
        c.Status = chemical.Status;
        c.PesticideUser = chemical.PesticideUser;
        c.FormulationType = chemical.FormulationType;
        c.FormulationSubType = chemical.FormulationSubType;
        c.BiocideAuthorisationType = chemical.BiocideAuthorisationType;
        c.PesticidePossibleUse = chemical.PesticidePossibleUse;
        c.PesticideProductGroup = chemical.PesticideProductGroup;
        c.BiocidePossibleUse = chemical.BiocidePossibleUse;
        c.BiocideSpecialUse = chemical.BiocideSpecialUse;
        c.BiocideProductType = chemical.BiocideProductType;
        c.BiocideUser = chemical.BiocideUser;
        c.PestControlType = chemical.PestControlType;
        c.BarcodeValue = chemical.BarcodeValue;
        c.BiocideProductGroup = chemical.BiocideProductGroup;
        if (!chemicalsDbContext.AuthorisationHolders.Any(x =>
                x.RemoteId == chemical.AuthorisationHolder.RemoteId))
        {
            var ah = new AuthorisationHolder
            {
                RemoteId = chemical.AuthorisationHolder.RemoteId,
                Name = chemical.AuthorisationHolder.Name,
                Address = chemical.AuthorisationHolder.Address
            };
            await ah.Create(chemicalsDbContext).ConfigureAwait(false);
            c.AuthorisationHolderId = ah.Id;
        }
        else
        {
            c.AuthorisationHolderId = chemicalsDbContext.AuthorisationHolders.First(x =>
                x.RemoteId == chemical.AuthorisationHolder.RemoteId).Id;
        }

        // Products carry no id in the feed: see ChemicalProductMatcher for how they are matched.
        // Local products the feed no longer lists are left untouched.
        await ChemicalProductMatcher.ReconcileAsync(chemical.Products?.ToList() ?? [], c.Products, c.Id,
            product => product.Update(chemicalsDbContext),
            product => product.Create(chemicalsDbContext)).ConfigureAwait(false);

        await c.Update(chemicalsDbContext).ConfigureAwait(false);

        // Null label/SDS arrays are an old feed: nothing changes. One bump of the chemical covers both copies.
        await ApplyDocumentsAsync(chemicalsDbContext, c.Id, chemical, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the feed's labels and per-language SDS, bumping the chemical once when either changed. A missing
    /// ChemicalDocuments table (MySQL 1146: the plugin has not migrated yet) skips the documents, logged once per run;
    /// the chemical and its products still sync.
    /// </summary>
    private static async Task ApplyDocumentsAsync(
        ChemicalsDbContext db, int localChemicalId, Chemical feed, CancellationToken ct)
    {
        try
        {
            // Documents and the bump commit together: a failure between them would leave documents saved with the
            // chemical's UpdatedAt not advanced, and a retry (which then finds nothing to change) would never repair it.
            // The context retries transient failures (EnableRetryOnFailure), so the unit runs through its strategy and
            // starts from a clean tracker on each attempt.
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var labelsChanged = await ChemicalLabelSync.ApplyAsync(db, localChemicalId, feed.Labels, ct, bumpChemical: false)
                    .ConfigureAwait(false);
                var sdsChanged = await ChemicalSdsLanguageSync.ApplyAsync(db, localChemicalId, feed.SdsDocuments, ct, bumpChemical: false)
                    .ConfigureAwait(false);
                if (labelsChanged || sdsChanged)
                {
                    await ChemicalDocumentFeedSync.BumpChemicalAsync(db, localChemicalId, ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (ChemicalSchemaGate.IsMissingTable(e))
        {
            // The transaction rolled back; drop the entities the failed save left tracked.
            db.ChangeTracker.Clear();
            if (Interlocked.CompareExchange(ref _missingTableLogged, 1, 0) == 0)
            {
                Console.WriteLine("info: ChemicalDocuments table is missing (plugin not migrated yet); skipping label and SDS documents this run");
            }
        }
    }
}
