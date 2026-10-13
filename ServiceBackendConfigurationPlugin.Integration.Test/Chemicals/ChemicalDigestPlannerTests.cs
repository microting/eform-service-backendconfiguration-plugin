namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using System.Collections.Generic;
using System.Linq;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

[TestFixture]
public class ChemicalDigestPlannerTests
{
    // Monday 2026-10-26 slot (07:00 CET = 06:00 UTC); the week's registrations are
    // the half-open window [previous slot, this slot).
    private static readonly DateOnly Today = new(2026, 10, 26);
    private static readonly DateTime NewSince = new(2026, 10, 19, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SlotUtc = new(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<int, ChemicalRegisterFacts> Register = new()
    {
        [1] = new ChemicalRegisterFacts(1, "Roundup Flex", "1-234", 5, null, Utc(2026, 9, 1)),   // possession passed
        [2] = new ChemicalRegisterFacts(2, "Karate Zeon", "2-345", 5, Utc(2026, 11, 20), null),  // sales in < 1 month
        [3] = new ChemicalRegisterFacts(3, "Harmløs", "3-456", 5, null, null),                  // nothing to act on
        [4] = new ChemicalRegisterFacts(4, "Afmeldt Middel", "4-567", 7, null, null),           // expired status
        [5] = new ChemicalRegisterFacts(5, "Basta", "5-678", 5, Utc(2027, 3, 1), null),          // sales in < 6 months
    };

    private static ChemicalDigestPlacement Placed(int placementId, int chemicalId, DateTime? registeredAt = null) =>
        new(placementId, chemicalId, "Kemirum", "Hylde 2", registeredAt ?? LongAgo);

    private static ChemicalDigestProperty Property(int id, string name, string[] recipients, params ChemicalDigestPlacement[] placements) =>
        new(id, name, recipients, placements);

    private static IReadOnlyList<ChemicalDigest> Build(params ChemicalDigestProperty[] properties) =>
        ChemicalDigestPlanner.Build(properties, Register, Today, NewSince, SlotUtc);

    [Test]
    public void OneEmailPerRecipient_WithThePassedItem()
    {
        var digests = Build(Property(10, "Gården", ["a@example.com", "b@example.com"], Placed(100, 1)));

        Assert.That(digests.Select(d => d.Recipient), Is.EqualTo(new[] { "a@example.com", "b@example.com" }));
        var item = digests[0].Sections.Single().Passed.Single();
        Assert.That(item, Is.EqualTo(new ChemicalDigestItem(100, "Roundup Flex", "1-234", "Kemirum", "Hylde 2",
            ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.UseAndPossession, null, new DateOnly(2026, 9, 1))));
    }

    [Test]
    public void RecipientOnTwoProperties_GetsOneEmailWithTwoSections_SortedByName()
    {
        var digests = Build(
            Property(11, "Nordgården", ["a@example.com"], Placed(101, 2)),
            Property(10, "Gården", ["a@example.com"], Placed(100, 1)));

        Assert.That(digests.Single().Sections.Select(s => s.PropertyName), Is.EqualTo(new[] { "Gården", "Nordgården" }));
    }

    [Test]
    public void OnlyNewRegistrations_NoEmail()
    {
        Assert.That(Build(Property(10, "Gården", ["a@example.com"], Placed(100, 3, registeredAt: NewSince.AddDays(1)))), Is.Empty);
    }

    [Test]
    public void NewRegistration_IsListedNextToActionItems()
    {
        var section = Build(Property(10, "Gården", ["a@example.com"],
            Placed(100, 1), Placed(102, 3, registeredAt: NewSince.AddDays(1)))).Single().Sections.Single();

        Assert.That(section.NewRegistrations.Select(i => i.PlacementId), Is.EqualTo(new[] { 102 }));
        Assert.That(section.NewRegistrations.Single().Threshold, Is.Null);
    }

    [Test]
    public void MovedPlacement_IsNotANewRegistration()
    {
        var section = Build(Property(10, "Gården", ["a@example.com"],
            Placed(100, 1), new ChemicalDigestPlacement(106, 3, "Lade", "", NewSince.AddDays(1), MovedFromPlacementId: 99)))
            .Single().Sections.Single();

        Assert.That(section.NewRegistrations, Is.Empty);
    }

    [Test]
    public void PropertyWithNothingToReport_IsOmitted()
    {
        var digest = Build(
            Property(10, "Gården", ["a@example.com"], Placed(100, 1)),
            Property(12, "Stille gård", ["a@example.com"], Placed(103, 3))).Single();

        Assert.That(digest.Sections.Select(s => s.PropertyId), Is.EqualTo(new[] { 10 }));
    }

    [Test]
    public void UpcomingAndStatus_AreSeparateLists_UpcomingSortedByDeadline()
    {
        var section = Build(Property(10, "Gården", ["a@example.com"], Placed(104, 5), Placed(101, 2), Placed(105, 4)))
            .Single().Sections.Single();

        Assert.That(section.Passed, Is.Empty);
        Assert.That(section.Upcoming.Select(i => (i.PlacementId, i.Threshold)), Is.EqualTo(new[]
        {
            (101, (ChemicalAlertThresholdEnum?)ChemicalAlertThresholdEnum.OneMonth),
            (104, (ChemicalAlertThresholdEnum?)ChemicalAlertThresholdEnum.SixMonths),
        }));
        Assert.That(section.StatusChanges.Single(), Is.EqualTo(new ChemicalDigestItem(105, "Afmeldt Middel", "4-567", "Kemirum",
            "Hylde 2", ChemicalAlertThresholdEnum.StatusChanged, null, 7, null)));
        Assert.That(section.ActionItems.Count(), Is.EqualTo(3));
    }

    [Test]
    public void ChemicalMissingFromRegister_IsSkipped()
    {
        Assert.That(Build(Property(10, "Gården", ["a@example.com"], Placed(100, 999))), Is.Empty);
    }

    [Test]
    public void PropertyWithoutRecipients_ProducesNothing()
    {
        Assert.That(Build(Property(10, "Gården", [], Placed(100, 1))), Is.Empty);
    }

    [Test]
    public void NewRegistrationWindow_IsHalfOpen_SlotBelongsToNextDigest()
    {
        var section = Build(Property(10, "Gården", ["a@example.com"],
                Placed(100, 1),
                Placed(102, 3, registeredAt: NewSince),                 // exactly the previous slot: this digest
                Placed(107, 3, registeredAt: NewSince.AddTicks(-1)),    // just before: last week's digest
                Placed(108, 3, registeredAt: SlotUtc.AddTicks(-1)),     // just before the slot: this digest
                Placed(109, 3, registeredAt: SlotUtc),                  // exactly this slot: the NEXT digest
                Placed(110, 3, registeredAt: SlotUtc.AddMinutes(20))))  // after the slot: the NEXT digest
            .Single().Sections.Single();

        Assert.That(section.NewRegistrations.Select(i => i.PlacementId), Is.EqualTo(new[] { 102, 108 }));
    }
}
