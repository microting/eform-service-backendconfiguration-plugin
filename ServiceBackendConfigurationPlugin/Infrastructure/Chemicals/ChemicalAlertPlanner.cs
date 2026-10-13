using System;
using System.Collections.Generic;
using System.Linq;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>
/// Decides which chemical pushes are due (flutter-chemistry spec §9, Plan D
/// decisions 3–5). Pure: the job loads, this decides, the job sends and logs.
/// </summary>
public static class ChemicalAlertPlanner
{
    /// <summary>Upper bound on a MovedFromPlacementId chain; also stops a corrupt cycle.</summary>
    public const int MaxLineageDepth = 100;

    public static IReadOnlyList<int> Lineage(int placementId, IReadOnlyDictionary<int, int?> movedFrom)
    {
        var lineage = new List<int> { placementId };
        var seen = new HashSet<int> { placementId };
        var current = placementId;
        while (lineage.Count < MaxLineageDepth
               && movedFrom.TryGetValue(current, out var parent)
               && parent is { } parentId
               && seen.Add(parentId))
        {
            lineage.Add(parentId);
            current = parentId;
        }

        return lineage;
    }

    public static ChemicalAlertPlan Plan(
        IReadOnlyList<ChemicalAlertPlacement> placements,
        IReadOnlyDictionary<int, ChemicalRegisterFacts> register,
        IReadOnlyCollection<ChemicalAlertLogKey> sentLog,
        DateOnly today)
    {
        var logByPlacement = sentLog.ToLookup(x => x.PlacementId);
        var due = new List<ChemicalDueAlert>();
        var observedStatusUpdates = new Dictionary<int, int?>();

        foreach (var placement in placements)
        {
            if (!register.TryGetValue(placement.ChemicalId, out var facts))
            {
                continue;
            }

            var history = placement.Lineage.SelectMany(id => logByPlacement[id]).ToList();

            PlanStatus(placement, facts, history, due, observedStatusUpdates);

            foreach (var kind in ChemicalRegisterFacts.DeadlineKinds)
            {
                var deadline = facts.Deadline(kind);
                if (ChemicalAlertRules.ReachedThreshold(deadline, today) is not { } threshold)
                {
                    continue;
                }

                // Enum order is severity order: SixMonths < OneMonth < Passed.
                if (history.Any(x => x.DeadlineKind == kind && x.Threshold >= threshold))
                {
                    continue;
                }

                due.Add(new ChemicalDueAlert(placement.PlacementId, placement.PropertyId, placement.ChemicalId,
                    facts.Name, threshold, kind, null, ChemicalAlertRules.DeadlineDate(deadline!.Value)));
            }
        }

        return new ChemicalAlertPlan(due, observedStatusUpdates);
    }

    private static void PlanStatus(
        ChemicalAlertPlacement placement, ChemicalRegisterFacts facts, IReadOnlyList<ChemicalAlertLogKey> history,
        List<ChemicalDueAlert> due, Dictionary<int, int?> observedStatusUpdates)
    {
        var current = facts.Status;
        if (current == placement.ObservedStatus)
        {
            return;
        }

        var alertDue = placement.ObservedStatus is not null
                       && ChemicalAlertRules.IsActionStatus(current)
                       && !history.Any(x => x.Threshold == ChemicalAlertThresholdEnum.StatusChanged && x.Status == current);
        if (alertDue)
        {
            due.Add(new ChemicalDueAlert(placement.PlacementId, placement.PropertyId, placement.ChemicalId,
                facts.Name, ChemicalAlertThresholdEnum.StatusChanged, null, current, null));
            return;
        }

        observedStatusUpdates[placement.PlacementId] = current;
    }
}
