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


namespace ServiceBackendConfigurationPlugin.Integration.Test
{
    using System.Collections.Generic;
    using System.Linq;
    using ChemicalsBase.Infrastructure.Data.Entities;
    using Microting.eForm.Infrastructure.Constants;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

    /// <summary>
    /// Product matching in the nightly chemical register sync (SearchListJob). The feed carries no
    /// product id, and one product = one SDS = one barcode. Products are matched by barcode, then by
    /// real SDS file name, then placeholder to placeholder, and never by Name or by count/position.
    /// </summary>
    [TestFixture]
    public class ChemicalProductMatcherTests
    {
        private const int ChemicalId = 7;
        private const string EmptyFileMd5 = "d41d8cd98f00b204e9800998ecf8427e";

        private static Product Feed(string barcode = null, string fileName = null, string name = null,
            string checksum = "sum") => new()
        {
            Barcode = barcode,
            FileName = fileName,
            Name = name,
            Checksum = checksum,
            IsActive = true,
            IsValid = true,
            Verified = true
        };

        private static Product LocalRow(int id, string barcode = null, string fileName = null, string name = null,
            string workflowState = null) => new()
        {
            Id = id,
            ChemicalId = ChemicalId,
            Barcode = barcode,
            FileName = fileName,
            Name = name,
            Checksum = "",
            WorkflowState = workflowState ?? Constants.WorkflowStates.Created
        };

        /// <summary>
        /// Runs one sync pass the way SearchListJob does: matched rows are updated in place, unmatched
        /// feed products become new local rows. Returns how many rows were created or changed.
        /// </summary>
        private static int Sync(IReadOnlyList<Product> feed, List<Product> locals)
        {
            var writes = 0;
            foreach (var match in ChemicalProductMatcher.Match(feed, locals))
            {
                if (match.Local == null)
                {
                    var created = ChemicalProductMatcher.CreateFrom(match.Feed, ChemicalId);
                    created.Id = locals.Count == 0 ? 1 : locals.Max(x => x.Id) + 1;
                    created.WorkflowState = Constants.WorkflowStates.Created;
                    locals.Add(created);
                    writes++;
                }
                else if (ChemicalProductMatcher.Apply(match.Feed, match.Local))
                {
                    writes++;
                }
            }

            return writes;
        }

        [Test]
        public void TwoNewProductsWithNullName_AndDistinctFileNamesAndBarcodes_BothSurvive()
        {
            var locals = new List<Product>();
            var feed = new[]
            {
                Feed("5701234567892", "sds-a.pdf"),
                Feed("5701234567885", "sds-b.pdf")
            };

            Sync(feed, locals);
            Sync(feed, locals);

            Assert.That(locals.Select(x => x.Barcode),
                Is.EquivalentTo(new[] { "5701234567892", "5701234567885" }));
            Assert.That(locals.Select(x => x.FileName), Is.EquivalentTo(new[] { "sds-a.pdf", "sds-b.pdf" }));
        }

        [Test]
        public void NullNamedNewProducts_NeverCollapseOntoOneLocalRow_WhenCountsAreEqual()
        {
            // The old equal-count branch matched by Name: null == null overwrote every row.
            var locals = new List<Product>
            {
                LocalRow(1, "5701234567892", "sds-a.pdf"),
                LocalRow(2, "5701234567885", "sds-b.pdf")
            };
            var feed = new[]
            {
                Feed("5701234567892", "sds-a.pdf"),
                Feed("5701234567885", "sds-b.pdf")
            };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Select(m => m.Local?.Id), Is.EqualTo(new int?[] { 1, 2 }));
        }

        [Test]
        public void FillingPlaceholderBarcode_WithRealFileName_UpdatesTheLocalPlaceholder()
        {
            var locals = new List<Product> { LocalRow(1, null, "sds-a.pdf") };
            var feed = new[] { Feed("5701234567892", "sds-a.pdf", name: "1 L") };

            Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Id, Is.EqualTo(1));
            Assert.That(locals[0].Barcode, Is.EqualTo("5701234567892"));
            Assert.That(locals[0].Name, Is.EqualTo("1 L"));
        }

        [Test]
        public void FillingPlaceholderBarcode_WithoutRealFileName_UpdatesTheLocalPlaceholder()
        {
            var locals = new List<Product> { LocalRow(1, null, EmptyFileMd5) };
            var feed = new[] { Feed("5701234567892", EmptyFileMd5) };

            Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Barcode, Is.EqualTo("5701234567892"));
        }

        [Test]
        public void PlaceholderFeedProduct_MatchesPlaceholderLocal()
        {
            var locals = new List<Product> { LocalRow(1, "  ", "") };
            var feed = new[] { Feed(null, null) };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Single().Local?.Id, Is.EqualTo(1));
        }

        [Test]
        public void AtMostOneFeedPlaceholder_ClaimsTheLocalPlaceholder()
        {
            var locals = new List<Product> { LocalRow(1) };
            var feed = new[] { Feed(null, null), Feed(null, EmptyFileMd5.ToUpperInvariant()) };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Select(m => m.Local?.Id), Is.EqualTo(new int?[] { 1, null }));
        }

        [Test]
        public void BarcodeMatch_WinsOverFileNameMatch()
        {
            var locals = new List<Product>
            {
                LocalRow(1, null, "sds-a.pdf"),
                LocalRow(2, "5701234567892", "sds-old.pdf")
            };
            var feed = new[] { Feed("5701234567892", "sds-a.pdf") };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Single().Local?.Id, Is.EqualTo(2));
        }

        [Test]
        public void BarcodeMatch_ClaimsFirst_EvenWhenAnEarlierFeedProductSharesTheFileName()
        {
            var locals = new List<Product>
            {
                LocalRow(1, "5701234567892", "sds-a.pdf")
            };
            var feed = new[]
            {
                Feed(null, "sds-a.pdf"),
                Feed("5701234567892", "sds-a.pdf")
            };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Select(m => m.Local?.Id), Is.EqualTo(new int?[] { null, 1 }));
        }

        [Test]
        public void FileNameMatch_NeverClaimsALocalWithADifferentBarcode()
        {
            // Same SDS, different size: a different barcode is a different product.
            var locals = new List<Product> { LocalRow(1, "5701234567892", "sds-a.pdf") };
            var feed = new[] { Feed("5701234567885", "sds-a.pdf") };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Single().Local, Is.Null);
        }

        [TestCase("036000291452", "0036000291452")]
        [TestCase("0036000291452", "036000291452")]
        [TestCase(" 036000291452 ", "0036000291452")]
        public void UpcA_And_ZeroPrefixedEan13_Match(string feedBarcode, string localBarcode)
        {
            var locals = new List<Product> { LocalRow(1, localBarcode, "sds-old.pdf") };
            var feed = new[] { Feed(feedBarcode, "sds-new.pdf") };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Single().Local?.Id, Is.EqualTo(1));
        }

        [Test]
        public void DifferentEan13Barcodes_DoNotMatch()
        {
            var locals = new List<Product> { LocalRow(1, "1036000291452", "sds-old.pdf") };
            var feed = new[] { Feed("036000291452", "sds-new.pdf") };

            Assert.That(ChemicalProductMatcher.Match(feed, locals).Single().Local, Is.Null);
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("   ", false)]
        [TestCase(EmptyFileMd5, false)]
        [TestCase("D41D8CD98F00B204E9800998ECF8427E", false)]
        [TestCase("sds-a.pdf", true)]
        public void IsRealFileName(string fileName, bool expected)
        {
            Assert.That(ChemicalProductMatcher.IsRealFileName(fileName), Is.EqualTo(expected));
        }

        [Test]
        public void EmptyFileMd5_NeverMatchesByFileName()
        {
            var locals = new List<Product> { LocalRow(1, null, EmptyFileMd5), LocalRow(2, null, EmptyFileMd5) };
            var feed = new[] { Feed(null, EmptyFileMd5), Feed(null, EmptyFileMd5) };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            // Placeholder to placeholder, at most one.
            Assert.That(matches.Select(m => m.Local?.Id), Is.EqualTo(new int?[] { 1, null }));
        }

        [Test]
        public void LegacyFeedWithTwoProductsSharingAFileName_NeverOverwritesOneLocalRowTwice()
        {
            var locals = new List<Product> { LocalRow(1, null, "sds-a.pdf", "1 L") };
            var feed = new[]
            {
                Feed(null, "sds-a.pdf", "1 L"),
                Feed(null, "sds-a.pdf", "5 L")
            };

            var matches = ChemicalProductMatcher.Match(feed, locals);

            Assert.That(matches.Select(m => m.Local?.Id), Is.EqualTo(new int?[] { 1, null }));
        }

        [Test]
        public void EachLocalRow_IsMatchedAtMostOnce()
        {
            var locals = new List<Product>
            {
                LocalRow(1, "5701234567892", "sds-a.pdf"),
                LocalRow(2)
            };
            var feed = new[]
            {
                Feed("5701234567892", "sds-a.pdf"),
                Feed("5701234567892", "sds-a.pdf"),
                Feed(null, "sds-a.pdf"),
                Feed(),
                Feed()
            };

            var matched = ChemicalProductMatcher.Match(feed, locals)
                .Where(m => m.Local != null).Select(m => m.Local.Id).ToList();

            Assert.That(matched, Is.Unique);
            Assert.That(matched, Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void NameIsNeverUsedForMatching()
        {
            var locals = new List<Product> { LocalRow(1, "5701234567892", "sds-a.pdf", "1 L") };
            var feed = new[] { Feed("5701234567885", "sds-b.pdf", "1 L") };

            Assert.That(ChemicalProductMatcher.Match(feed, locals).Single().Local, Is.Null);
        }

        [Test]
        public void RemovedLocalRows_AreNeverMatched()
        {
            var locals = new List<Product>
            {
                LocalRow(1, "5701234567892", "sds-a.pdf", workflowState: Constants.WorkflowStates.Removed)
            };
            var feed = new[] { Feed("5701234567892", "sds-a.pdf") };

            Assert.That(ChemicalProductMatcher.Match(feed, locals).Single().Local, Is.Null);
        }

        [Test]
        public void UnmatchedLocalRows_AreLeftUntouched()
        {
            var untouched = LocalRow(1, "5701234567892", "sds-a.pdf", "1 L");
            var locals = new List<Product> { untouched };
            var feed = new[] { Feed("5701234567885", "sds-b.pdf", "5 L") };

            Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(2));
            Assert.That(untouched.Barcode, Is.EqualTo("5701234567892"));
            Assert.That(untouched.FileName, Is.EqualTo("sds-a.pdf"));
            Assert.That(untouched.Name, Is.EqualTo("1 L"));
            Assert.That(untouched.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Created));
        }

        [Test]
        public void MatchedRow_TakesEveryProductFieldFromTheFeed()
        {
            var local = LocalRow(1, null, "sds-a.pdf", "old");
            var feed = new Product
            {
                Barcode = "5701234567892",
                FileName = "sds-a.pdf",
                Name = null,
                Checksum = "abc",
                IsActive = true,
                IsValid = true,
                Verified = true
            };

            var changed = ChemicalProductMatcher.Apply(feed, local);

            Assert.That(changed, Is.True);
            Assert.That(local.Barcode, Is.EqualTo("5701234567892"));
            Assert.That(local.FileName, Is.EqualTo("sds-a.pdf"));
            Assert.That(local.Name, Is.Null);
            Assert.That(local.Checksum, Is.EqualTo("abc"));
            Assert.That(local.IsActive, Is.True);
            Assert.That(local.IsValid, Is.True);
            Assert.That(local.Verified, Is.True);
            Assert.That(local.Id, Is.EqualTo(1));
            Assert.That(local.ChemicalId, Is.EqualTo(ChemicalId));
        }

        [Test]
        public void CreatedRow_TakesEveryProductFieldFromTheFeed()
        {
            var created = ChemicalProductMatcher.CreateFrom(Feed("5701234567892", "sds-a.pdf", "1 L", "abc"),
                ChemicalId);

            Assert.That(created.ChemicalId, Is.EqualTo(ChemicalId));
            Assert.That(created.Id, Is.EqualTo(0));
            Assert.That(created.Barcode, Is.EqualTo("5701234567892"));
            Assert.That(created.FileName, Is.EqualTo("sds-a.pdf"));
            Assert.That(created.Name, Is.EqualTo("1 L"));
            Assert.That(created.Checksum, Is.EqualTo("abc"));
            Assert.That(created.IsActive && created.IsValid && created.Verified, Is.True);
        }

        [Test]
        public void NullFeedFileNameAndChecksum_AreStoredAsEmpty()
        {
            // Both columns are non-nullable on the tenant DB.
            var created = ChemicalProductMatcher.CreateFrom(Feed(null, null, checksum: null), ChemicalId);

            Assert.That(created.FileName, Is.EqualTo(""));
            Assert.That(created.Checksum, Is.EqualTo(""));
        }

        [Test]
        public void SecondRun_IsIdempotent()
        {
            var locals = new List<Product>
            {
                LocalRow(1, null, "sds-a.pdf", "1 L"),
                LocalRow(2, "036000291452", "sds-b.pdf"),
                LocalRow(3),
                LocalRow(4, "5701234567878", "sds-gone.pdf")
            };
            var feed = new[]
            {
                Feed("5701234567892", "sds-a.pdf"),
                Feed("0036000291452", "sds-b.pdf", "5 L"),
                Feed(null, "sds-c.pdf"),
                Feed(null, "sds-c.pdf"),
                Feed(),
                Feed("5701234567885", EmptyFileMd5)
            };

            var firstWrites = Sync(feed, locals);
            var snapshot = locals.Select(x => (x.Id, x.Barcode, x.FileName, x.Name)).ToList();
            var secondWrites = Sync(feed, locals);

            Assert.That(firstWrites, Is.GreaterThan(0));
            Assert.That(secondWrites, Is.EqualTo(0));
            Assert.That(locals.Select(x => (x.Id, x.Barcode, x.FileName, x.Name)), Is.EqualTo(snapshot));
            Assert.That(locals, Has.Count.EqualTo(7));
        }

        [Test]
        public void Match_IsDeterministic_RegardlessOfLocalOrder()
        {
            var a = LocalRow(1, null, "sds-a.pdf");
            var b = LocalRow(2, null, "sds-a.pdf");
            var feed = new[] { Feed(null, "sds-a.pdf", "first") };

            var forward = ChemicalProductMatcher.Match(feed, new[] { a, b });
            var reversed = ChemicalProductMatcher.Match(feed, new[] { b, a });

            Assert.That(forward.Single().Local?.Id, Is.EqualTo(1));
            Assert.That(reversed.Single().Local?.Id, Is.EqualTo(1));
        }

        [Test]
        public void Match_ReturnsOneEntryPerFeedProduct_InFeedOrder()
        {
            var feed = new[] { Feed("5701234567892"), Feed(null, "sds-a.pdf"), Feed() };

            var matches = ChemicalProductMatcher.Match(feed, new List<Product>());

            Assert.That(matches.Select(m => m.Feed), Is.EqualTo(feed));
            Assert.That(matches.All(m => m.Local == null), Is.True);
        }
    }
}
