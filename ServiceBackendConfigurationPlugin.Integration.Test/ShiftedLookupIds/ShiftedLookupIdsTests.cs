namespace ServiceBackendConfigurationPlugin.Integration.Test.ShiftedLookupIds;

using System.Collections.Generic;
using System.Threading.Tasks;
using Microting.eForm.Infrastructure.Data.Entities;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Handlers;
using static ShiftedLookupIdsDatabases;

/// <summary>
/// Issue microting/eform-angular-frontend#8141: on installs where the SDK
/// lookup tables were seeded with a non-1 auto_increment step, Danish is not
/// LanguageId 1. Danish text must be resolved through LanguageCode "da".
/// </summary>
[TestFixture]
[NonParallelizable]
public class ShiftedLookupIdsTests
{
    [Test]
    public async Task FloatingLayerStatus_ReturnsTheDanishOptionText_WhenDanishIsNotLanguageId1()
    {
        int fieldId;
        await using (var sdk = Sdk())
        {
            var field = new Field { FieldTypeId = SingleSelectFieldTypeId, Label = "Status" };
            sdk.Fields.Add(field);
            await sdk.SaveChangesAsync();
            fieldId = field.Id;
            sdk.FieldOptions.Add(new FieldOption
            {
                FieldId = fieldId, Key = "1", DisplayOrder = "0",
                FieldOptionTranslations = new List<FieldOptionTranslation>
                {
                    new() { LanguageId = EnglishId, Text = "Slurry tank stirred" },
                    new() { LanguageId = DanishId, Text = "Beholder omrørt" },
                    new() { LanguageId = GermanId, Text = "Güllebehälter gerührt" },
                },
            });
            await sdk.SaveChangesAsync();
        }

        await using var sdkDbContext = Sdk();
        var text = await FloatingLayerCaseCompletedHandler.GetDanishFieldOptionTextAsync(sdkDbContext, fieldId, "1");

        Assert.That(text, Is.EqualTo("Beholder omrørt"));
    }
}
