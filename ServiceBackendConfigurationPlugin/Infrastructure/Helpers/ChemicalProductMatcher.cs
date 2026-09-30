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
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Constants;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>A feed product and the local product it updates, or null when a new one is created.</summary>
public sealed record ProductMatch(Product Feed, Product? Local);

/// <summary>
/// Matches the products of one chemical in the chemicalbase feed to the tenant's local products.
/// The feed carries no product id; one product = one SDS = one barcode. Each step walks the still
/// unmatched feed products in feed order and gives each the first unclaimed local row (by Id) that
/// fits, so a local row is claimed at most once:
/// 1. same barcode and same real SDS file (two sizes can share a barcode centrally);
/// 2. same barcode (canonical form: UPC-A == "0" + UPC-A as EAN-13);
/// 3. same real SDS file, onto a row with no barcode yet or the same barcode;
/// 4. a feed product without a real SDS onto a local placeholder (neither barcode nor real SDS),
///    pure placeholders first, so a barcode filled in centrally updates the tenant's placeholder;
/// 5. central corrections, only when unambiguous (exactly one candidate either way):
///    a. a local row whose barcode is gone from the feed takes the feed product with its real SDS
///       (barcode corrected or cleared);
///    b. a local row whose real SDS is gone from the feed takes the feed product with its barcode,
///       or the barcode-less one when it has none (SDS replaced or deleted);
/// 6. any other feed product onto a leftover local placeholder (an SDS uploaded onto a placeholder;
///    chemicalbase never hard-deletes products, so a leftover placeholder was filled in).
/// Name and product count/position are never used. Unmatched local rows are not touched.
/// </summary>
public static class ChemicalProductMatcher
{
    /// <summary>MD5 of an empty file: chemicalbase's file name for a product without an SDS.</summary>
    private const string EmptyFileMd5 = "d41d8cd98f00b204e9800998ecf8427e";

    /// <summary>A product with its match keys computed once.</summary>
    private sealed record Keyed(Product Row, int Index, string? Barcode, string? File)
    {
        public bool IsPlaceholder => Barcode == null && File == null;

        public bool SameBarcode(Keyed other) => Barcode != null && Barcode == other.Barcode;

        public bool SameFile(Keyed other) =>
            File != null && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRealFileName(string? fileName) => NormalizedFileName(fileName) != null;

    /// <summary>Trimmed real SDS file name; null when blank or the empty-file MD5.</summary>
    private static string? NormalizedFileName(string? fileName)
    {
        var trimmed = fileName?.Trim();
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, EmptyFileMd5, StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    /// <summary>Trimmed barcode, with a 12-digit UPC-A widened to its EAN-13 form; null when blank.</summary>
    private static string? CanonicalBarcode(string? barcode)
    {
        var trimmed = barcode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length == 12 && trimmed.All(char.IsAsciiDigit) ? "0" + trimmed : trimmed;
    }

    private static Keyed Key(Product product, int index) =>
        new(product, index, CanonicalBarcode(product.Barcode), NormalizedFileName(product.FileName));

    /// <summary>One entry per feed product, in feed order.</summary>
    public static IReadOnlyList<ProductMatch> Match(IReadOnlyList<Product> feed, IEnumerable<Product> locals)
    {
        var feedKeys = feed.Select(Key).ToList();
        var localKeys = locals
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .OrderBy(x => x.Id)
            .Select(Key)
            .ToList();
        var matched = new Keyed?[feedKeys.Count];
        var claimed = new HashSet<Keyed>();

        IEnumerable<Keyed> Unmatched(IEnumerable<Keyed> feedOrder) => feedOrder.Where(f => matched[f.Index] == null);

        void Claim(Keyed f, Keyed local)
        {
            matched[f.Index] = local;
            claimed.Add(local);
        }

        void ClaimFirstFit(IEnumerable<Keyed> feedOrder, Func<Keyed, Keyed, bool> fits)
        {
            foreach (var f in Unmatched(feedOrder).ToList())
            {
                var local = localKeys.FirstOrDefault(l => !claimed.Contains(l) && fits(f, l));
                if (local != null)
                {
                    Claim(f, local);
                }
            }
        }

        // A correction applies only when the local row has exactly one candidate and that feed
        // product is wanted by no other eligible local row.
        void ClaimUnambiguous(Func<Keyed, bool> eligible, Func<Keyed, Keyed, bool> isCandidate)
        {
            var proposals = localKeys
                .Where(l => !claimed.Contains(l) && eligible(l))
                .Select(l => (Local: l, Candidates: Unmatched(feedKeys).Where(f => isCandidate(l, f)).ToList()))
                .ToList();
            foreach (var (local, candidates) in proposals)
            {
                if (candidates.Count == 1 && proposals.Count(p => p.Candidates.Contains(candidates[0])) == 1)
                {
                    Claim(candidates[0], local);
                }
            }
        }

        ClaimFirstFit(feedKeys, (f, l) => f.SameBarcode(l) && f.SameFile(l));
        ClaimFirstFit(feedKeys, (f, l) => f.SameBarcode(l));
        ClaimFirstFit(feedKeys, (f, l) => f.SameFile(l) && (l.Barcode == null || l.Barcode == f.Barcode));
        ClaimFirstFit(feedKeys.Where(f => f.File == null).OrderBy(f => f.Barcode != null), (_, l) => l.IsPlaceholder);
        ClaimUnambiguous(
            l => l.Barcode != null && l.File != null && !feedKeys.Any(f => f.SameBarcode(l)),
            (l, f) => l.SameFile(f));
        ClaimUnambiguous(
            l => l.File != null && !feedKeys.Any(f => f.SameFile(l)),
            (l, f) => l.Barcode == f.Barcode);
        ClaimFirstFit(feedKeys, (_, l) => l.IsPlaceholder);

        return feedKeys.Select(f => new ProductMatch(f.Row, matched[f.Index]?.Row)).ToList();
    }

    /// <summary>
    /// Matches, then updates each changed local row and creates a row for each unmatched feed
    /// product, in feed order. Each row is changed just before its own <paramref name="update"/>.
    /// </summary>
    public static async Task ReconcileAsync(IReadOnlyList<Product> feed, IEnumerable<Product> locals, int chemicalId,
        Func<Product, Task> update, Func<Product, Task> create)
    {
        foreach (var match in Match(feed, locals))
        {
            if (match.Local == null)
            {
                await create(CreateFrom(match.Feed, chemicalId)).ConfigureAwait(false);
            }
            else if (Apply(match.Feed, match.Local))
            {
                await update(match.Local).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Copies the fields the feed sends (Barcode, FileName, Name, Checksum, Verified) onto the local
    /// row; true when anything changed. IsActive/IsValid are not in the feed and are left alone.
    /// </summary>
    public static bool Apply(Product feed, Product local)
    {
        var incoming = (feed.Barcode, FileName: feed.FileName ?? "", feed.Name, Checksum: feed.Checksum ?? "",
            feed.Verified);
        if ((local.Barcode, local.FileName, local.Name, local.Checksum, local.Verified) == incoming)
        {
            return false;
        }

        (local.Barcode, local.FileName, local.Name, local.Checksum, local.Verified) = incoming;
        return true;
    }

    /// <summary>A new local row for an unmatched feed product (the feed's own ids are not reused).</summary>
    private static Product CreateFrom(Product feed, int chemicalId)
    {
        // A fresh row's FileName/Checksum are null, so Apply always assigns every field.
        var product = new Product { ChemicalId = chemicalId };
        Apply(feed, product);
        return product;
    }
}
