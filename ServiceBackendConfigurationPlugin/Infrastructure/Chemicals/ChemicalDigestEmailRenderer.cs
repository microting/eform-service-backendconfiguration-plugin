using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>The weekly digest e-mail, in Danish (Plan D Contract C5). Every stored text is HTML-encoded.</summary>
public static class ChemicalDigestEmailRenderer
{
    public static string W1Url(string customerNo, int propertyId) =>
        $"https://{customerNo}.microting.com/plugins/backend-configuration-pn/chemicals" +
        $"?propertyId={propertyId.ToString(CultureInfo.InvariantCulture)}&filter=requires-action";

    public static ChemicalDigestEmail Render(ChemicalDigest digest, string customerNo, DateOnly today)
    {
        var week = ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue));
        var html = new StringBuilder();
        var text = new StringBuilder();

        html.Append("<!DOCTYPE html><html lang=\"da\"><head><meta charset=\"utf-8\"></head>")
            .Append("<body style=\"font-family:Arial,sans-serif;color:#1a1a1a\">")
            .Append("<h1 style=\"font-size:20px\">Ugentlig kemioversigt</h1>")
            .Append($"<p>Oversigt for kunde {E(customerNo)} pr. {Date(today)}.</p>");
        text.AppendLine("Ugentlig kemioversigt")
            .AppendLine($"Oversigt for kunde {customerNo} pr. {Date(today)}.")
            .AppendLine();

        foreach (var section in digest.Sections)
        {
            var url = W1Url(customerNo, section.PropertyId);
            html.Append($"<h2 style=\"font-size:17px;margin-top:24px\">{E(section.PropertyName)}</h2>");
            text.AppendLine(section.PropertyName);
            AppendList(html, text, "Frist overskredet", section.Passed);
            AppendList(html, text, "Frist inden for 6 måneder", section.Upcoming);
            AppendList(html, text, "Afmeldt eller udløbet", section.StatusChanges);
            AppendList(html, text, "Nye registreringer denne uge", section.NewRegistrations);
            html.Append($"<p><a href=\"{E(url)}\">Åbn Kemikalier – beholdning</a></p>");
            text.AppendLine($"Åbn Kemikalier – beholdning: {url}").AppendLine();
        }

        const string footer = "Du modtager denne e-mail, fordi din adresse står som modtager af den ugentlige " +
                              "oversigt under Ejendomsindstillinger – Kemi.";
        html.Append($"<p style=\"color:#666;font-size:12px\">{footer}</p></body></html>");
        text.AppendLine(footer);

        return new ChemicalDigestEmail(digest.Recipient, $"Kemi: ugentlig oversigt uge {week}", html.ToString(), text.ToString());
    }

    /// <summary>"Roundup Flex (reg.nr. 1-234) – Kemirum, Hylde 2 – salgsfrist 01-03-2027".</summary>
    public static string Line(ChemicalDigestItem item)
    {
        var where = string.IsNullOrWhiteSpace(item.PlacementNote)
            ? item.LocationName
            : $"{item.LocationName}, {item.PlacementNote}";
        return $"{item.ChemicalName} (reg.nr. {item.RegistrationNo}) – {where}{Detail(item)}";
    }

    private static string Detail(ChemicalDigestItem item) => item.Threshold switch
    {
        ChemicalAlertThresholdEnum.Passed when item.DeadlineKind == ChemicalDeadlineKindEnum.UseAndPossession =>
            $" – {DeadlineLabel(item.DeadlineKind)} overskredet {Date(item.Deadline!.Value)} – skal bortskaffes som farligt affald",
        ChemicalAlertThresholdEnum.Passed => $" – {DeadlineLabel(item.DeadlineKind)} overskredet {Date(item.Deadline!.Value)}",
        ChemicalAlertThresholdEnum.SixMonths or ChemicalAlertThresholdEnum.OneMonth =>
            $" – {DeadlineLabel(item.DeadlineKind)} {Date(item.Deadline!.Value)}",
        ChemicalAlertThresholdEnum.StatusChanged => item.Status == ChemicalAlertRules.StatusExpired ? " – udløbet" : " – afmeldt",
        _ => string.Empty
    };

    private static string DeadlineLabel(ChemicalDeadlineKindEnum? kind) =>
        kind == ChemicalDeadlineKindEnum.Sales ? "salgsfrist" : "brugs- og besiddelsesfrist";

    private static void AppendList(StringBuilder html, StringBuilder text, string heading, IReadOnlyList<ChemicalDigestItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        html.Append($"<h3 style=\"font-size:15px\">{heading}</h3><ul>");
        text.AppendLine(heading);
        foreach (var item in items)
        {
            var line = Line(item);
            html.Append($"<li>{E(line)}</li>");
            text.AppendLine($"- {line}");
        }

        html.Append("</ul>");
    }

    private static string Date(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    private static string E(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
