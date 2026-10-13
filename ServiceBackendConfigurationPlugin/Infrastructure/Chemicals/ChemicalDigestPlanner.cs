using System;
using System.Collections.Generic;
using System.Linq;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>
/// Builds the weekly digests: one per recipient address, one section per
/// property listing that address, returned only when a section needs action
/// (spec §9). Passed deadlines and Afmeldt/Udløbet products repeat every week
/// until the placement is removed (Plan D decision 10). New registrations are the
/// half-open window [newFromUtc, newUntilUtc), i.e. [previous slot, this slot), so
/// an item registered exactly at the slot belongs to the next digest.
/// </summary>
public static class ChemicalDigestPlanner
{
    public static IReadOnlyList<ChemicalDigest> Build(
        IReadOnlyList<ChemicalDigestProperty> properties,
        IReadOnlyDictionary<int, ChemicalRegisterFacts> register,
        DateOnly today,
        DateTime newFromUtc,
        DateTime newUntilUtc)
    {
        var sections = properties
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => (Property: p, Section: BuildSection(p, register, today, newFromUtc, newUntilUtc)))
            .Where(x => !x.Section.IsEmpty)
            .ToList();

        return sections
            .SelectMany(x => x.Property.Recipients.Select(recipient => (Recipient: recipient, x.Section)))
            .GroupBy(x => x.Recipient, StringComparer.Ordinal)
            .Select(g => new ChemicalDigest(g.Key, g.Select(x => x.Section).ToList()))
            .Where(d => d.Sections.Any(s => s.HasAction))
            .OrderBy(d => d.Recipient, StringComparer.Ordinal)
            .ToList();
    }

    private static ChemicalDigestSection BuildSection(
        ChemicalDigestProperty property, IReadOnlyDictionary<int, ChemicalRegisterFacts> register,
        DateOnly today, DateTime newFromUtc, DateTime newUntilUtc)
    {
        var passed = new List<ChemicalDigestItem>();
        var upcoming = new List<ChemicalDigestItem>();
        var statusChanges = new List<ChemicalDigestItem>();
        var newRegistrations = new List<ChemicalDigestItem>();

        foreach (var placement in property.Placements)
        {
            if (!register.TryGetValue(placement.ChemicalId, out var facts))
            {
                continue;
            }

            ChemicalDigestItem Item(ChemicalAlertThresholdEnum? threshold, ChemicalDeadlineKindEnum? kind, int? status, DateOnly? deadline) =>
                new(placement.PlacementId, facts.Name, facts.RegistrationNo, placement.LocationName, placement.PlacementNote,
                    threshold, kind, status, deadline);

            foreach (var kind in ChemicalRegisterFacts.DeadlineKinds)
            {
                var deadline = facts.Deadline(kind);
                if (ChemicalAlertRules.ReachedThreshold(deadline, today) is not { } threshold)
                {
                    continue;
                }

                var item = Item(threshold, kind, null, ChemicalAlertRules.DeadlineDate(deadline!.Value));
                (threshold == ChemicalAlertThresholdEnum.Passed ? passed : upcoming).Add(item);
            }

            if (ChemicalAlertRules.IsActionStatus(facts.Status))
            {
                statusChanges.Add(Item(ChemicalAlertThresholdEnum.StatusChanged, null, facts.Status, null));
            }

            if (placement.MovedFromPlacementId == null && placement.RegisteredAt >= newFromUtc && placement.RegisteredAt < newUntilUtc)
            {
                newRegistrations.Add(Item(null, null, null, null));
            }
        }

        return new ChemicalDigestSection(property.PropertyId, property.Name,
            ByDeadline(passed), ByDeadline(upcoming), ByName(statusChanges), ByName(newRegistrations));
    }

    private static List<ChemicalDigestItem> ByDeadline(IEnumerable<ChemicalDigestItem> items) =>
        items.OrderBy(i => i.Deadline).ThenBy(i => i.ChemicalName, StringComparer.OrdinalIgnoreCase).ToList();

    private static List<ChemicalDigestItem> ByName(IEnumerable<ChemicalDigestItem> items) =>
        items.OrderBy(i => i.ChemicalName, StringComparer.OrdinalIgnoreCase).ToList();
}
