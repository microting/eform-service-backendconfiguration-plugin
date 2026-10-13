using System;
using System.Collections.Generic;
using System.Globalization;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>
/// Push texts in the worker's language: Danish for "da*", English otherwise
/// (Plan D decision 14, the service's da/default SharedResource split).
/// Product and property names are shown as stored (BMD content stays Danish).
/// </summary>
public sealed class ChemicalAlertTexts
{
    public static readonly ChemicalAlertTexts Danish = new()
    {
        DeadlineTitles = new Dictionary<(ChemicalDeadlineKindEnum, ChemicalAlertThresholdEnum), string>
        {
            [(ChemicalDeadlineKindEnum.Sales, ChemicalAlertThresholdEnum.SixMonths)] = "Salgsfrist om under 6 måneder",
            [(ChemicalDeadlineKindEnum.Sales, ChemicalAlertThresholdEnum.OneMonth)] = "Salgsfrist om under 1 måned",
            [(ChemicalDeadlineKindEnum.Sales, ChemicalAlertThresholdEnum.Passed)] = "Salgsfristen er overskredet",
            [(ChemicalDeadlineKindEnum.UseAndPossession, ChemicalAlertThresholdEnum.SixMonths)] = "Brugs- og besiddelsesfrist om under 6 måneder",
            [(ChemicalDeadlineKindEnum.UseAndPossession, ChemicalAlertThresholdEnum.OneMonth)] = "Brugs- og besiddelsesfrist om under 1 måned",
            [(ChemicalDeadlineKindEnum.UseAndPossession, ChemicalAlertThresholdEnum.Passed)] = "Brugs- og besiddelsesfristen er overskredet",
        },
        DeregisteredTitle = "Produktet er afmeldt",
        ExpiredTitle = "Produktet er udløbet",
        DeadlineLabel = "Frist",
        DisposeNote = "Skal bortskaffes som farligt affald.",
        SummaryTitleFormat = "{0} kemikalier kræver handling",
        DateFormat = "dd-MM-yyyy",
    };

    public static readonly ChemicalAlertTexts English = new()
    {
        DeadlineTitles = new Dictionary<(ChemicalDeadlineKindEnum, ChemicalAlertThresholdEnum), string>
        {
            [(ChemicalDeadlineKindEnum.Sales, ChemicalAlertThresholdEnum.SixMonths)] = "Sales deadline in less than 6 months",
            [(ChemicalDeadlineKindEnum.Sales, ChemicalAlertThresholdEnum.OneMonth)] = "Sales deadline in less than 1 month",
            [(ChemicalDeadlineKindEnum.Sales, ChemicalAlertThresholdEnum.Passed)] = "Sales deadline has passed",
            [(ChemicalDeadlineKindEnum.UseAndPossession, ChemicalAlertThresholdEnum.SixMonths)] = "Use and possession deadline in less than 6 months",
            [(ChemicalDeadlineKindEnum.UseAndPossession, ChemicalAlertThresholdEnum.OneMonth)] = "Use and possession deadline in less than 1 month",
            [(ChemicalDeadlineKindEnum.UseAndPossession, ChemicalAlertThresholdEnum.Passed)] = "Use and possession deadline has passed",
        },
        DeregisteredTitle = "Product deregistered",
        ExpiredTitle = "Product expired",
        DeadlineLabel = "Deadline",
        DisposeNote = "Dispose of as hazardous waste.",
        SummaryTitleFormat = "{0} chemicals need action",
        DateFormat = "yyyy-MM-dd",
    };

    private ChemicalAlertTexts()
    {
    }

    private IReadOnlyDictionary<(ChemicalDeadlineKindEnum, ChemicalAlertThresholdEnum), string> DeadlineTitles { get; init; }
    private string DeregisteredTitle { get; init; }
    private string ExpiredTitle { get; init; }
    private string DeadlineLabel { get; init; }
    private string DisposeNote { get; init; }
    private string SummaryTitleFormat { get; init; }
    private string DateFormat { get; init; }

    public static ChemicalAlertTexts For(string languageCode) =>
        languageCode != null && languageCode.StartsWith("da", StringComparison.OrdinalIgnoreCase) ? Danish : English;

    public string Title(ChemicalDueAlert alert) =>
        alert.Threshold == ChemicalAlertThresholdEnum.StatusChanged
            ? alert.Status == ChemicalAlertRules.StatusExpired ? ExpiredTitle : DeregisteredTitle
            : DeadlineTitles[(alert.DeadlineKind!.Value, alert.Threshold)];

    public string Body(ChemicalDueAlert alert, string propertyName)
    {
        var subject = $"{alert.ChemicalName} – {propertyName}";
        if (alert.Deadline is not { } deadline)
        {
            return subject;
        }

        var body = $"{subject}. {DeadlineLabel}: {deadline.ToString(DateFormat, CultureInfo.InvariantCulture)}";
        return alert is { Threshold: ChemicalAlertThresholdEnum.Passed, DeadlineKind: ChemicalDeadlineKindEnum.UseAndPossession }
            ? $"{body}. {DisposeNote}"
            : body;
    }

    public string SummaryTitle(int count) => string.Format(CultureInfo.InvariantCulture, SummaryTitleFormat, count);
}
