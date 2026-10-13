using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>What the chemical jobs need from the eForm SDK database.</summary>
public interface IChemicalSdkDirectory
{
    /// <summary>SDK site id → Languages.LanguageCode ("da", "en-US", …); unknown sites are absent.</summary>
    Task<IReadOnlyDictionary<int, string>> LanguageCodesAsync(IReadOnlyCollection<int> sdkSiteIds);

    /// <summary>The tenant's customer number (SDK setting "customerNo"), used in web links.</summary>
    Task<string> CustomerNoAsync();
}

public sealed class SdkChemicalDirectory(Func<MicrotingDbContext> sdkDbContextFactory) : IChemicalSdkDirectory
{
    public async Task<IReadOnlyDictionary<int, string>> LanguageCodesAsync(IReadOnlyCollection<int> sdkSiteIds)
    {
        var ids = sdkSiteIds.ToList();
        await using var sdk = sdkDbContextFactory();
        return await sdk.Sites.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                Code = sdk.Languages.Where(l => l.Id == s.LanguageId).Select(l => l.LanguageCode).FirstOrDefault()
            })
            .ToDictionaryAsync(x => x.Id, x => x.Code ?? string.Empty);
    }

    public async Task<string> CustomerNoAsync()
    {
        await using var sdk = sdkDbContextFactory();
        return await sdk.Settings.AsNoTracking()
            .Where(x => x.Name == "customerNo")
            .Select(x => x.Value)
            .SingleAsync();
    }
}
