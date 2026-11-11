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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>Runs the label and SDS copies for one chemical with a single bump, and survives a missing documents table.</summary>
public static class ChemicalFeedDocuments
{
    private static int _missingTableLogged;

    /// <summary>How often the missing-table skip was logged since <see cref="ResetRunLatch"/> (once per run, not per chemical).</summary>
    public static int MissingTableLogCount => Volatile.Read(ref _missingTableLogged);

    /// <summary>Called at the start of each feed run so the missing-table line is logged once per run.</summary>
    public static void ResetRunLatch() => Volatile.Write(ref _missingTableLogged, 0);

    /// <summary>
    /// Applies labels and SDS documents; the chemical is bumped once when either changed. A missing ChemicalDocuments
    /// table (MySQL 1146: the plugin has not migrated yet) skips the documents and never fails the chemical sync.
    /// </summary>
    public static async Task<bool> ApplyAsync(ChemicalsDbContext db, int localChemicalId,
        IReadOnlyList<ChemicalLabelFeedItem>? labels, IReadOnlyList<ChemicalSdsFeedItem>? sdsDocuments,
        CancellationToken ct)
    {
        try
        {
            var labelsChanged = await ChemicalLabelSync.ApplyAsync(db, localChemicalId, labels, ct, bumpChemical: false)
                .ConfigureAwait(false);
            var sdsChanged = await ChemicalSdsLanguageSync.ApplyAsync(db, localChemicalId, sdsDocuments, ct, bumpChemical: false)
                .ConfigureAwait(false);
            if (!labelsChanged && !sdsChanged)
            {
                return false;
            }

            await ChemicalDocumentFeedSync.BumpChemicalAsync(db, localChemicalId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (ChemicalSchemaGate.IsMissingTable(e))
        {
            // The failed save leaves its entities tracked; drop them so the caller's context stays usable.
            db.ChangeTracker.Clear();
            if (Interlocked.CompareExchange(ref _missingTableLogged, 1, 0) == 0)
            {
                Console.WriteLine("info: ChemicalDocuments table is missing (plugin not migrated yet); skipping label and SDS documents this run");
            }

            return false;
        }
    }
}
