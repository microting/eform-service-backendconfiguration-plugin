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
    using System.Threading.Tasks;
    using ChemicalsBase.Infrastructure.Data.Entities;
    using Microting.eForm.Infrastructure.Constants;
    using NUnit.Framework;
    using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

    /// <summary>
    /// Product matching in the nightly chemical register sync (SearchListJob). The feed carries no
    /// product id, and one product = one SDS = one barcode. Products are matched by barcode, then by
    /// real SDS file name, then placeholder, then central corrections; never by Name or position.
    /// </summary>
    [TestFixture]
    public class ChemicalProductMatcherTests
    {
        private const int ChemicalId = 7;

        // Pinned literal on purpose (the matcher keeps its own private copy).
        private const string EmptyFileMd5 = "d41d8cd98f00b204e9800998ecf8427e";

        /// <summary>
        /// Mirrors the real feed projection: Barcode, Name, FileName, Checksum, Verified. IsActive and
        /// IsValid are never sent, so they deserialize as false. Checksum defaults to a non-empty
        /// value so a first sync onto a fresh local row registers as a change.
        /// </summary>
        private static Product Feed(string barcode = null, string fileName = null, string name = null,
            string checksum = "sum") => new()
        {
            Barcode = barcode,
            FileName = fileName,
            Name = name,
            Checksum = checksum,
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
        /// Runs the production reconcile step with recording delegates in place of EF: created rows
        /// get the next Id and join the local list. Returns how many rows were created or updated.
        /// </summary>
        private static async Task<int> Sync(IReadOnlyList<Product> feed, List<Product> locals)
        {
            var writes = 0;
            await ChemicalProductMatcher.ReconcileAsync(feed, locals.ToList(), ChemicalId,
                update: _ =>
                {
                    writes++;
                    return Task.CompletedTask;
                },
                create: created =>
                {
                    created.Id = locals.Count == 0 ? 1 : locals.Max(x => x.Id) + 1;
                    created.WorkflowState = Constants.WorkflowStates.Created;
                    locals.Add(created);
                    writes++;
                    return Task.CompletedTask;
                });
            return writes;
        }

        private static int?[] MatchedIds(IReadOnlyList<Product> feed, IReadOnlyList<Product> locals) =>
            ChemicalProductMatcher.Match(feed, locals).Select(m => m.Local?.Id).ToArray();

        // ---- barcode ----

        [Test]
        public async Task TwoNewProductsWithNullName_AndDistinctFileNamesAndBarcodes_BothSurvive()
        {
            var locals = new List<Product>();
            var feed = new[]
            {
                Feed("5701234567892", "sds-a.pdf"),
                Feed("5701234567885", "sds-b.pdf")
            };

            await Sync(feed, locals);
            await Sync(feed, locals);

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

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 1, 2 }));
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

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 2 }));
        }

        [Test]
        public void BarcodeMatch_ClaimsFirst_EvenWhenAnEarlierFeedProductSharesTheFileName()
        {
            var locals = new List<Product> { LocalRow(1, "5701234567892", "sds-a.pdf") };
            var feed = new[]
            {
                Feed(null, "sds-a.pdf"),
                Feed("5701234567892", "sds-a.pdf")
            };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null, 1 }));
        }

        [Test]
        public void SharedBarcode_PrefersTheLocalRowWithTheSameSds_WhenLocalsAreCrosswise()
        {
            // Two sizes sharing one barcode (10 live chemicals): the SDS decides, never the Id order.
            var locals = new List<Product>
            {
                LocalRow(1, "3362130037422", "sds-10l.pdf", "10 L"),
                LocalRow(2, "3362130037422", "sds-5l.pdf", "5 L")
            };
            var feed = new[]
            {
                Feed("3362130037422", "sds-5l.pdf", "5 L"),
                Feed("3362130037422", "sds-10l.pdf", "10 L")
            };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 2, 1 }));
        }

        [TestCase("036000291452", "0036000291452")]
        [TestCase("0036000291452", "036000291452")]
        [TestCase(" 036000291452 ", "0036000291452")]
        public void UpcA_And_ZeroPrefixedEan13_Match(string feedBarcode, string localBarcode)
        {
            var locals = new List<Product> { LocalRow(1, localBarcode, "sds-old.pdf") };
            var feed = new[] { Feed(feedBarcode, "sds-new.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 1 }));
        }

        [Test]
        public void DifferentEan13Barcodes_DoNotMatch()
        {
            var locals = new List<Product> { LocalRow(1, "1036000291452", "sds-old.pdf") };
            var feed = new[] { Feed("036000291452", "sds-new.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null }));
        }

        // ---- SDS file name ----

        [Test]
        public async Task BarcodeAddedToAProductWithAnSds_UpdatesThatRow()
        {
            var locals = new List<Product> { LocalRow(1, null, "sds-a.pdf") };
            var feed = new[] { Feed("5701234567892", "sds-a.pdf", name: "1 L") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Id, Is.EqualTo(1));
            Assert.That(locals[0].Barcode, Is.EqualTo("5701234567892"));
            Assert.That(locals[0].Name, Is.EqualTo("1 L"));
        }

        [Test]
        public void FileNameMatch_NeverClaimsALocalWhoseDifferentBarcodeIsStillInTheFeed()
        {
            // Same SDS, different size: a different barcode that is still live is a different product.
            var locals = new List<Product>
            {
                LocalRow(1, "5701234567892", "sds-a.pdf"),
                LocalRow(2, "5701234567892", "sds-b.pdf")
            };
            var feed = new[]
            {
                Feed("5701234567892", "sds-b.pdf"),
                Feed("5701234567885", "sds-a.pdf")
            };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 2, null }));
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("   ", false)]
        [TestCase(EmptyFileMd5, false)]
        [TestCase("D41D8CD98F00B204E9800998ECF8427E", false)]
        [TestCase("sds-a.pdf", true)]
        public void IsRealFileName_ReturnsExpected(string fileName, bool expected)
        {
            Assert.That(ChemicalProductMatcher.IsRealFileName(fileName), Is.EqualTo(expected));
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

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 1, null }));
        }

        // ---- placeholders ----

        [Test]
        public void PlaceholderFeedProduct_MatchesPlaceholderLocal()
        {
            var locals = new List<Product> { LocalRow(1, "  ", "") };
            var feed = new[] { Feed(null, null) };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 1 }));
        }

        [Test]
        public void AtMostOneFeedPlaceholder_ClaimsTheLocalPlaceholder()
        {
            var locals = new List<Product> { LocalRow(1) };
            var feed = new[] { Feed(null, null), Feed(null, EmptyFileMd5.ToUpperInvariant()) };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 1, null }));
        }

        [Test]
        public void EmptyFileMd5_IsAPlaceholder_AndPlaceholdersPairOneToOne()
        {
            var locals = new List<Product> { LocalRow(1, null, EmptyFileMd5), LocalRow(2, null, EmptyFileMd5) };
            var feed = new[] { Feed(null, EmptyFileMd5), Feed(null, EmptyFileMd5) };

            // One-to-one; otherwise every run would create another placeholder row.
            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 1, 2 }));
        }

        [Test]
        public void PurePlaceholdersPairFirst_EvenWhenABarcodedPlaceholderPrecedesThemInTheFeed()
        {
            var locals = new List<Product> { LocalRow(1) };
            var feed = new[] { Feed("5701234567892", ""), Feed(null, "") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null, 1 }));
        }

        [Test]
        public async Task BarcodeFilledOnPlaceholder_WithoutRealFileName_UpdatesTheLocalPlaceholder()
        {
            var locals = new List<Product> { LocalRow(1, null, EmptyFileMd5) };
            var feed = new[] { Feed("5701234567892", EmptyFileMd5) };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Barcode, Is.EqualTo("5701234567892"));
        }

        [Test]
        public async Task SdsUploadedOntoPlaceholder_UpdatesTheLocalPlaceholder()
        {
            var locals = new List<Product> { LocalRow(1, null, "") };
            var feed = new[] { Feed(null, "sds-h.pdf") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Id, Is.EqualTo(1));
            Assert.That(locals[0].FileName, Is.EqualTo("sds-h.pdf"));
        }

        [Test]
        public async Task BarcodeAndSdsAddedTheSameDay_UpdateTheLocalPlaceholder()
        {
            var locals = new List<Product> { LocalRow(1, null, "") };
            var feed = new[] { Feed("5701234567892", "sds-h.pdf") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Id, Is.EqualTo(1));
            Assert.That(locals[0].Barcode, Is.EqualTo("5701234567892"));
            Assert.That(locals[0].FileName, Is.EqualTo("sds-h.pdf"));
        }

        // ---- central corrections (owner decision) ----

        [Test]
        public async Task BarcodeCorrectedCentrally_UpdatesTheRowInPlace()
        {
            var locals = new List<Product> { LocalRow(1, "5701234567892", "sds-a.pdf") };
            var feed = new[] { Feed("5701234567885", "sds-a.pdf") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Barcode, Is.EqualTo("5701234567885"));
        }

        [Test]
        public async Task BarcodeClearedCentrally_UpdatesTheRowInPlace()
        {
            var locals = new List<Product> { LocalRow(1, "5701234567892", "sds-a.pdf") };
            var feed = new[] { Feed(null, "sds-a.pdf") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].Barcode, Is.Null);
        }

        [Test]
        public void BarcodeCorrection_IsNotApplied_WhenTwoFeedProductsShareTheSds()
        {
            var locals = new List<Product> { LocalRow(1, "5701234567892", "sds-a.pdf") };
            var feed = new[] { Feed("5701234567885", "sds-a.pdf"), Feed("5701234567878", "sds-a.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null, null }));
        }

        [Test]
        public void BarcodeCorrection_IsNotApplied_WhenTwoLocalRowsCompeteForOneFeedProduct()
        {
            var locals = new List<Product>
            {
                LocalRow(1, "5701234567892", "sds-a.pdf"),
                LocalRow(2, "5701234567878", "sds-a.pdf")
            };
            var feed = new[] { Feed("5701234567885", "sds-a.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null }));
        }

        [Test]
        public async Task SdsReplacedCentrally_OnABarcodelessProduct_UpdatesTheRowInPlace()
        {
            var locals = new List<Product> { LocalRow(1, null, "sds-old.pdf") };
            var feed = new[] { Feed(null, "sds-new.pdf") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].FileName, Is.EqualTo("sds-new.pdf"));
        }

        [Test]
        public async Task SdsDeletedCentrally_UpdatesTheRowInPlace()
        {
            var locals = new List<Product> { LocalRow(1, null, "sds-wrong.pdf") };
            var feed = new[] { Feed(null, "") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(1));
            Assert.That(locals[0].FileName, Is.EqualTo(""));
        }

        [Test]
        public void SdsCorrection_IsNotApplied_WhenTwoFeedProductsAreCandidates()
        {
            var locals = new List<Product> { LocalRow(1, null, "sds-old.pdf") };
            var feed = new[] { Feed(null, "sds-new-1.pdf"), Feed(null, "sds-new-2.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null, null }));
        }

        [Test]
        public void SdsCorrection_IsNotApplied_WhenTwoLocalRowsCompeteForOneFeedProduct()
        {
            var locals = new List<Product> { LocalRow(1, null, "sds-old-1.pdf"), LocalRow(2, null, "sds-old-2.pdf") };
            var feed = new[] { Feed(null, "sds-new.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null }));
        }

        [Test]
        public void SdsCorrection_IsNotApplied_WhenTheLocalSdsIsStillInTheFeed()
        {
            var locals = new List<Product>
            {
                LocalRow(1, null, "sds-a.pdf"),
                LocalRow(2, null, "sds-a.pdf")
            };
            var feed = new[] { Feed(null, "sds-a.pdf"), Feed(null, "sds-b.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { 1, null }));
        }

        // ---- general properties ----

        [Test]
        public void EachLocalRow_IsMatchedAtMostOnce()
        {
            // Property check across all steps (overlaps the ordered cases above on purpose).
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

            var matched = MatchedIds(feed, locals).Where(x => x != null).ToList();

            Assert.That(matched, Is.Unique);
            Assert.That(matched, Is.EquivalentTo(new int?[] { 1, 2 }));
        }

        [Test]
        public void NameIsNeverUsedForMatching()
        {
            var locals = new List<Product> { LocalRow(1, "5701234567892", "sds-a.pdf", "1 L") };
            var feed = new[] { Feed("5701234567885", "sds-b.pdf", "1 L") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null }));
        }

        [Test]
        public void RemovedLocalRows_AreNeverMatched()
        {
            var locals = new List<Product>
            {
                LocalRow(1, "5701234567892", "sds-a.pdf", workflowState: Constants.WorkflowStates.Removed)
            };
            var feed = new[] { Feed("5701234567892", "sds-a.pdf") };

            Assert.That(MatchedIds(feed, locals), Is.EqualTo(new int?[] { null }));
        }

        [Test]
        public async Task UnmatchedLocalRows_AreLeftUntouched()
        {
            var untouched = LocalRow(1, "5701234567892", "sds-a.pdf", "1 L");
            var locals = new List<Product> { untouched };
            var feed = new[] { Feed("5701234567885", "sds-b.pdf", "5 L") };

            await Sync(feed, locals);

            Assert.That(locals, Has.Count.EqualTo(2));
            Assert.That(untouched.Barcode, Is.EqualTo("5701234567892"));
            Assert.That(untouched.FileName, Is.EqualTo("sds-a.pdf"));
            Assert.That(untouched.Name, Is.EqualTo("1 L"));
            Assert.That(untouched.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Created));
        }

        [Test]
        public void MatchedRow_TakesTheFeedFields_AndKeepsIsActiveAndIsValid()
        {
            var local = LocalRow(1, null, "sds-a.pdf", "old");
            local.IsActive = true;
            local.IsValid = true;

            var changed = ChemicalProductMatcher.Apply(Feed("5701234567892", "sds-a.pdf", null, "abc"), local);

            Assert.That(changed, Is.True);
            Assert.That(local.Barcode, Is.EqualTo("5701234567892"));
            Assert.That(local.FileName, Is.EqualTo("sds-a.pdf"));
            Assert.That(local.Name, Is.Null);
            Assert.That(local.Checksum, Is.EqualTo("abc"));
            Assert.That(local.Verified, Is.True);
            // The feed never sends IsActive/IsValid; the sync must not reset them.
            Assert.That(local.IsActive, Is.True);
            Assert.That(local.IsValid, Is.True);
            Assert.That(local.Id, Is.EqualTo(1));
            Assert.That(local.ChemicalId, Is.EqualTo(ChemicalId));
        }

        [Test]
        public void MatchedRow_ThatAlreadyEqualsTheFeed_IsNotChanged()
        {
            var local = LocalRow(1, "5701234567892", "sds-a.pdf", "1 L");
            local.Checksum = "sum";
            local.Verified = true;

            Assert.That(ChemicalProductMatcher.Apply(Feed("5701234567892", "sds-a.pdf", "1 L"), local), Is.False);
        }

        [Test]
        public async Task CreatedRow_TakesTheFeedFields()
        {
            var locals = new List<Product>();

            await Sync(new[] { Feed("5701234567892", "sds-a.pdf", "1 L", "abc") }, locals);

            var created = locals.Single();
            Assert.That(created.ChemicalId, Is.EqualTo(ChemicalId));
            Assert.That(created.Barcode, Is.EqualTo("5701234567892"));
            Assert.That(created.FileName, Is.EqualTo("sds-a.pdf"));
            Assert.That(created.Name, Is.EqualTo("1 L"));
            Assert.That(created.Checksum, Is.EqualTo("abc"));
            Assert.That(created.Verified, Is.True);
        }

        [Test]
        public async Task NullFeedFileNameAndChecksum_AreStoredAsEmpty()
        {
            // Both columns are non-nullable on the tenant DB.
            var locals = new List<Product>();

            await Sync(new[] { Feed(null, null, checksum: null) }, locals);

            Assert.That(locals.Single().FileName, Is.EqualTo(""));
            Assert.That(locals.Single().Checksum, Is.EqualTo(""));
        }

        [Test]
        public async Task SecondRun_IsIdempotent()
        {
            // 4 locals + 6 feed products: 3 feed products match (1, 2, 3), local 4 stays untouched,
            // and 3 are created (two sds-c rows and the barcoded no-SDS row, since the only
            // placeholder is taken by the pure placeholder) = 7 rows.
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

            var firstWrites = await Sync(feed, locals);
            var snapshot = locals.Select(x => (x.Id, x.Barcode, x.FileName, x.Name)).ToList();
            var secondWrites = await Sync(feed, locals);

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

            Assert.That(MatchedIds(feed, new[] { a, b }), Is.EqualTo(new int?[] { 1 }));
            Assert.That(MatchedIds(feed, new[] { b, a }), Is.EqualTo(new int?[] { 1 }));
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
