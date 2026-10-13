using System;
using System.Collections.Generic;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>
/// An open placement as the alert job sees it. Lineage is this placement's id
/// first, then every placement it was moved from (MovedFromPlacementId chain):
/// alert history follows the product across moves.
/// </summary>
public sealed record ChemicalAlertPlacement(
    int PlacementId, int PropertyId, int ChemicalId, int? ObservedStatus, IReadOnlyList<int> Lineage);

/// <summary>What the customer's register copy says about one chemical.</summary>
public sealed record ChemicalRegisterFacts(
    int ChemicalId, string Name, string RegistrationNo, int? Status,
    DateTime? SalesDeadline, DateTime? UseAndPossessionDeadline)
{
    /// <summary>Deadline kinds in emission order; shared by the alert and digest planners.</summary>
    public static IReadOnlyList<ChemicalDeadlineKindEnum> DeadlineKinds { get; } =
        [ChemicalDeadlineKindEnum.UseAndPossession, ChemicalDeadlineKindEnum.Sales];

    public DateTime? Deadline(ChemicalDeadlineKindEnum kind) =>
        kind == ChemicalDeadlineKindEnum.Sales ? SalesDeadline : UseAndPossessionDeadline;
}

/// <summary>An existing ChemicalAlertLog row of one channel, reduced to its dedupe key.</summary>
public sealed record ChemicalAlertLogKey(
    int PlacementId, ChemicalAlertThresholdEnum Threshold, ChemicalDeadlineKindEnum? DeadlineKind, int? Status);

/// <summary>One alert to send now. Deadline is null for StatusChanged; Status is set only for StatusChanged.</summary>
public sealed record ChemicalDueAlert(
    int PlacementId, int PropertyId, int ChemicalId, string ChemicalName,
    ChemicalAlertThresholdEnum Threshold, ChemicalDeadlineKindEnum? DeadlineKind, int? Status, DateOnly? Deadline);

/// <param name="DueAlerts">Alerts to send in this run.</param>
/// <param name="ObservedStatusUpdates">
/// placementId → status to store right away: baselines and changes that need
/// no alert. A status that DOES need an alert is stored together with its log.
/// </param>
public sealed record ChemicalAlertPlan(
    IReadOnlyList<ChemicalDueAlert> DueAlerts, IReadOnlyDictionary<int, int?> ObservedStatusUpdates);
