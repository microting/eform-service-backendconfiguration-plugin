#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>Reads the customer's register copy (written only by the 02:00 UTC sync).</summary>
public static class ChemicalRegisterFactsReader
{
    /// <returns>The facts per chemical id, or null when the tenant's chemical schema is not migrated yet (skip the tenant).</returns>
    public static async Task<IReadOnlyDictionary<int, ChemicalRegisterFacts>?> LoadAsync(
        ChemicalDbContextHelper helper, IReadOnlyCollection<int> chemicalIds)
    {
        var ids = chemicalIds.ToList();
        await using var chemicals = helper.GetDbContext();
        if (!await ChemicalSchemaGate.IsMigratedAsync(chemicals).ConfigureAwait(false))
        {
            return null;
        }

        // WorkflowState is ignored on purpose (Plan A decision 12): a product
        // removed from the register while still placed keeps its deadlines.
        return await chemicals.Chemicals.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new ChemicalRegisterFacts(
                c.Id, c.Name, c.RegistrationNo, c.Status, c.SalesDeadline, c.UseAndPossesionDeadline))
            .ToDictionaryAsync(x => x.ChemicalId);
    }
}
