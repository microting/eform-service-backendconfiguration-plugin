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

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>Copies the feed's per-language SDS into the tenant's ChemicalDocuments. Products.FileName is not touched here.</summary>
public static class ChemicalSdsLanguageSync
{
    private static readonly IReadOnlySet<ChemicalDocumentType> Scope =
        new HashSet<ChemicalDocumentType> { ChemicalDocumentType.Sds };

    /// <summary><c>null</c> is an old feed and changes nothing.</summary>
    public static Task<bool> ApplyAsync(ChemicalsDbContext db, int localChemicalId,
        IReadOnlyList<ChemicalSdsFeedItem>? feed, CancellationToken ct, bool bumpChemical = true) =>
        ChemicalDocumentFeedSync.ApplyAsync(db, localChemicalId, Scope, feed?.Select(ToDocument).ToList(), ct,
            bumpChemical);

    private static ChemicalDocument ToDocument(ChemicalSdsFeedItem sds) => new()
    {
        Type = ChemicalDocumentType.Sds,
        Source = ChemicalDocumentSource.Supplier,
        Status = ChemicalDocumentStatus.Active,
        Language = sds.Language,
        ContentMd5 = sds.FileName,
        SourceRevisionDate = sds.RevisionDate,
        RetrievedAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
    };
}
