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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;
using ChemicalsBase.Infrastructure.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// The one generic sync behind the label (Task 13) and per-language SDS (Task 13b) copies. Tenant rows only mirror the
/// central feed (the tenant Source is not provenance; that lives centrally).
/// </summary>
public static class ChemicalDocumentFeedSync
{
    /// <summary>
    /// Syncs ONLY the local rows whose Type is in <paramref name="scope"/>; rows of other types are never touched (F48).
    /// <c>null</c> <paramref name="desired"/> is an old feed: no change. A removed local row that the feed lists again
    /// is revived through Update (never re-created: the unique index covers removed rows too, F6).
    /// With <paramref name="bumpChemical"/> the chemical's UpdatedAt is advanced when anything changed so the app's
    /// incremental sync re-sends it (F5); a caller running several syncs bumps once itself.
    /// </summary>
    /// <returns>True when anything changed.</returns>
    public static async Task<bool> ApplyAsync(ChemicalsDbContext db, int localChemicalId,
        IReadOnlySet<ChemicalDocumentType> scope, IReadOnlyList<ChemicalDocument>? desired, CancellationToken ct,
        bool bumpChemical = true)
    {
        if (desired == null)
        {
            return false;
        }

        var types = scope.ToList();
        var locals = await db.ChemicalDocuments
            .Where(d => d.ChemicalId == localChemicalId && types.Contains(d.Type))
            .ToListAsync(ct).ConfigureAwait(false);

        var changed = false;
        var seen = new HashSet<(ChemicalDocumentType Type, string Md5)>();
        foreach (var want in desired.Where(d => scope.Contains(d.Type)))
        {
            // The unique index is (chemical, type, md5), without Language: one md5 is one tenant row, so an md5 the
            // feed lists again (for another language) is skipped instead of re-created.
            if (!seen.Add((want.Type, want.ContentMd5)))
            {
                continue;
            }

            var row = FindLocal(locals, want);
            if (row == null)
            {
                want.ChemicalId = localChemicalId;
                await want.Create(db).ConfigureAwait(false);
                changed = true;
            }
            else if (Differs(row, want))
            {
                ApplyFeedFields(row, want);
                await row.Update(db).ConfigureAwait(false);
                changed = true;
            }
        }

        foreach (var gone in locals.Where(l => l.WorkflowState != Constants.WorkflowStates.Removed
                                               && !seen.Contains((l.Type, l.ContentMd5))))
        {
            await gone.Delete(db).ConfigureAwait(false);
            changed = true;
        }

        if (changed && bumpChemical)
        {
            await BumpChemicalAsync(db, localChemicalId, ct).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>
    /// PnBase.Update saves nothing without tracked changes, so UpdatedAt is set explicitly first; the app's incremental
    /// sync then re-sends the chemical.
    /// </summary>
    public static async Task BumpChemicalAsync(ChemicalsDbContext db, int localChemicalId, CancellationToken ct)
    {
        var chemical = await db.Chemicals.SingleAsync(c => c.Id == localChemicalId, ct).ConfigureAwait(false);
        chemical.UpdatedAt = DateTime.UtcNow;
        await chemical.Update(db).ConfigureAwait(false);
    }

    /// <summary>The local row for the feed item, removed or not: it is revived through Update, never re-created.</summary>
    private static ChemicalDocument? FindLocal(IEnumerable<ChemicalDocument> locals, ChemicalDocument want) =>
        locals.FirstOrDefault(l => l.Type == want.Type && l.ContentMd5 == want.ContentMd5);

    /// <summary>The fields <see cref="Differs"/> compares; keep the two in step.</summary>
    private static void ApplyFeedFields(ChemicalDocument row, ChemicalDocument want)
    {
        row.Language = want.Language;
        row.Source = want.Source;
        row.Status = ChemicalDocumentStatus.Active;
        row.SourcePackaging = want.SourcePackaging;
        row.SourceRevisionDate = want.SourceRevisionDate;
        row.SourceRevision = want.SourceRevision;
        row.WorkflowState = Constants.WorkflowStates.Created;
        row.LastSeenAt = DateTime.UtcNow;
    }

    private static bool Differs(ChemicalDocument row, ChemicalDocument want) =>
        row.WorkflowState == Constants.WorkflowStates.Removed
        || row.Status != ChemicalDocumentStatus.Active
        || row.Source != want.Source
        || row.Language != want.Language
        || row.SourcePackaging != want.SourcePackaging
        || row.SourceRevisionDate != want.SourceRevisionDate
        || row.SourceRevision != want.SourceRevision;
}
