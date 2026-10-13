namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Factories;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

[TestFixture]
[NonParallelizable]
public class ChemicalSdkDirectoryTests
{
    private static MicrotingDbContext Sdk() =>
        new MicrotingDbContextFactory().CreateDbContext([ChemicalJobDatabases.ConnectionString(ChemicalJobDatabases.SdkDb)]);

    [Test]
    public async Task LanguageCodes_MapsEachSiteToItsLanguage_AndSkipsUnknownSites()
    {
        int anna, bo;
        await using (var sdk = Sdk())
        {
            var danish = await LanguageAsync(sdk, "da", "Dansk");
            var english = await LanguageAsync(sdk, "en-US", "English");
            var annaSite = new Site { Name = "Anna", LanguageId = danish.Id };
            var boSite = new Site { Name = "Bo", LanguageId = english.Id };
            sdk.Sites.AddRange(annaSite, boSite);
            await sdk.SaveChangesAsync();
            anna = annaSite.Id;
            bo = boSite.Id;
        }

        var codes = await new ChemicalSdkDirectory(Sdk).LanguageCodesAsync([anna, bo, 987654]);

        Assert.That(codes, Is.EqualTo(new Dictionary<int, string> { [anna] = "da", [bo] = "en-US" }));
    }

    [Test]
    public async Task CustomerNo_ReadsTheSdkSetting()
    {
        await using (var sdk = Sdk())
        {
            var setting = await sdk.Settings.FirstOrDefaultAsync(x => x.Name == "customerNo");
            if (setting == null)
            {
                sdk.Settings.Add(new Setting { Name = "customerNo", Value = "4711" });
            }
            else
            {
                setting.Value = "4711";
            }

            await sdk.SaveChangesAsync();
        }

        Assert.That(await new ChemicalSdkDirectory(Sdk).CustomerNoAsync(), Is.EqualTo("4711"));
    }

    private static async Task<Language> LanguageAsync(MicrotingDbContext sdk, string code, string name)
    {
        var language = await sdk.Languages.FirstOrDefaultAsync(x => x.LanguageCode == code);
        if (language != null)
        {
            return language;
        }

        language = new Language { LanguageCode = code, Name = name };
        sdk.Languages.Add(language);
        await sdk.SaveChangesAsync();
        return language;
    }
}
