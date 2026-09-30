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
using ChemicalsBase.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Constants;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>A feed product and the local product it updates, or null when a new one is created.</summary>
public sealed record ProductMatch(Product Feed, Product? Local);

/// <summary>
/// Matches the products of one chemical in the chemicalbase feed to the tenant's local products.
/// The feed carries no product id; one product = one SDS = one barcode. Matching runs in passes,
/// each in feed order and then local Id order, and claims each local row at most once:
/// 1. barcode (canonical form, UPC-A == "0" + UPC-A as EAN-13);
/// 2. real SDS file name, onto a local row with no barcode yet or the same barcode;
/// 3. placeholder: a feed product without a real file name onto a local row with neither barcode
///    nor real file name. Feed products without a barcode go first, so filling in the barcode of
///    a placeholder product in chemicalbase updates the tenant's placeholder row.
/// Name and product count/position are never used. Unmatched local rows are not touched.
/// </summary>
public static class ChemicalProductMatcher
{
    /// <summary>MD5 of an empty file: chemicalbase's file name for a product without an SDS.</summary>
    private const string EmptyFileMd5 = "d41d8cd98f00b204e9800998ecf8427e";

    public static bool IsRealFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        !string.Equals(fileName.Trim(), EmptyFileMd5, StringComparison.OrdinalIgnoreCase);

    /// <summary>Trimmed barcode, with a 12-digit UPC-A widened to its EAN-13 form; null when blank.</summary>
    private static string? CanonicalBarcode(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return null;
        }

        var trimmed = barcode.Trim();
        return trimmed.Length == 12 && trimmed.All(char.IsAsciiDigit) ? "0" + trimmed : trimmed;
    }

    private static string? RealFileName(string? fileName) => IsRealFileName(fileName) ? fileName!.Trim() : null;

    /// <summary>One entry per feed product, in feed order.</summary>
    public static IReadOnlyList<ProductMatch> Match(IReadOnlyList<Product> feed, IReadOnlyList<Product> locals)
    {
        var candidates = locals
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .OrderBy(x => x.Id)
            .ToList();
        var result = new Product?[feed.Count];
        var claimed = new HashSet<Product>(ReferenceEqualityComparer.Instance);

        void Pass(Func<Product, bool> feedFilter, Func<Product, Product, bool> isMatch)
        {
            for (var i = 0; i < feed.Count; i++)
            {
                if (result[i] != null || !feedFilter(feed[i]))
                {
                    continue;
                }

                var local = candidates.FirstOrDefault(x => !claimed.Contains(x) && isMatch(feed[i], x));
                if (local != null)
                {
                    result[i] = local;
                    claimed.Add(local);
                }
            }
        }

        Pass(f => CanonicalBarcode(f.Barcode) != null,
            (f, l) => CanonicalBarcode(l.Barcode) == CanonicalBarcode(f.Barcode));
        Pass(f => RealFileName(f.FileName) != null,
            (f, l) => string.Equals(RealFileName(l.FileName), RealFileName(f.FileName),
                          StringComparison.OrdinalIgnoreCase) &&
                      (CanonicalBarcode(l.Barcode) == null ||
                       CanonicalBarcode(l.Barcode) == CanonicalBarcode(f.Barcode)));
        Pass(f => RealFileName(f.FileName) == null && CanonicalBarcode(f.Barcode) == null, IsPlaceholderPair);
        Pass(f => RealFileName(f.FileName) == null, IsPlaceholderPair);

        return feed.Select((f, i) => new ProductMatch(f, result[i])).ToList();
    }

    private static bool IsPlaceholderPair(Product feed, Product local) =>
        CanonicalBarcode(local.Barcode) == null && RealFileName(local.FileName) == null;

    /// <summary>Copies the feed's product fields onto the local row; true when anything changed.</summary>
    public static bool Apply(Product feed, Product local)
    {
        var fileName = feed.FileName ?? "";
        var checksum = feed.Checksum ?? "";
        if (local.Barcode == feed.Barcode && local.FileName == fileName && local.Name == feed.Name &&
            local.Checksum == checksum && local.IsActive == feed.IsActive && local.IsValid == feed.IsValid &&
            local.Verified == feed.Verified)
        {
            return false;
        }

        local.Barcode = feed.Barcode;
        local.FileName = fileName;
        local.Name = feed.Name;
        local.Checksum = checksum;
        local.IsActive = feed.IsActive;
        local.IsValid = feed.IsValid;
        local.Verified = feed.Verified;
        return true;
    }

    /// <summary>A new local row for an unmatched feed product (the feed's own ids are not reused).</summary>
    public static Product CreateFrom(Product feed, int chemicalId)
    {
        var product = new Product { ChemicalId = chemicalId };
        Apply(feed, product);
        return product;
    }
}
