using System;
using System.Collections.Generic;
using System.Linq;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>An open placement. MovedFromPlacementId is set when a move created it
/// (Plan A Task 12); such a placement is never a "new registration".</summary>
public sealed record ChemicalDigestPlacement(
    int PlacementId, int ChemicalId, string LocationName, string PlacementNote, DateTime RegisteredAt,
    int? MovedFromPlacementId = null);

public sealed record ChemicalDigestProperty(
    int PropertyId, string Name, IReadOnlyList<string> Recipients, IReadOnlyList<ChemicalDigestPlacement> Placements);

/// <summary>One line in a digest section. Threshold is null for a new registration.</summary>
public sealed record ChemicalDigestItem(
    int PlacementId, string ChemicalName, string RegistrationNo, string LocationName, string PlacementNote,
    ChemicalAlertThresholdEnum? Threshold, ChemicalDeadlineKindEnum? DeadlineKind, int? Status, DateOnly? Deadline);

public sealed record ChemicalDigestSection(
    int PropertyId, string PropertyName,
    IReadOnlyList<ChemicalDigestItem> Passed,
    IReadOnlyList<ChemicalDigestItem> Upcoming,
    IReadOnlyList<ChemicalDigestItem> StatusChanges,
    IReadOnlyList<ChemicalDigestItem> NewRegistrations)
{
    /// <summary>Items that need action; these are what the digest logs (Channel Email).</summary>
    public IEnumerable<ChemicalDigestItem> ActionItems => Passed.Concat(Upcoming).Concat(StatusChanges);

    public bool HasAction => ActionItems.Any();

    public bool IsEmpty => !HasAction && NewRegistrations.Count == 0;
}

public sealed record ChemicalDigest(string Recipient, IReadOnlyList<ChemicalDigestSection> Sections);

public sealed record ChemicalDigestEmail(string To, string Subject, string Html, string PlainText);
