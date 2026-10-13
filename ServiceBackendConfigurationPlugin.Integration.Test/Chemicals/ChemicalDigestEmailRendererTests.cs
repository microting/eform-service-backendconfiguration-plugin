namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using System.Net;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

[TestFixture]
public class ChemicalDigestEmailRendererTests
{
    private static readonly DateOnly Monday = new(2026, 10, 26); // ISO week 44

    private static ChemicalDigestItem PassedPossession(string name = "Roundup Flex", string note = "Hylde 2") =>
        new(100, name, "1-234", "Kemirum", note, ChemicalAlertThresholdEnum.Passed,
            ChemicalDeadlineKindEnum.UseAndPossession, null, new DateOnly(2026, 9, 1));

    private static ChemicalDigest Digest(params ChemicalDigestItem[] passed) =>
        new("a@example.com", [new ChemicalDigestSection(10, "Gården", passed, [], [], [])]);

    [Test]
    public void Render_AddressSubjectAndSection()
    {
        var email = ChemicalDigestEmailRenderer.Render(Digest(PassedPossession()), "4711", Monday);

        Assert.That(email.To, Is.EqualTo("a@example.com"));
        Assert.That(email.Subject, Is.EqualTo("Kemi: ugentlig oversigt uge 44"));
        Assert.That(email.PlainText, Does.Contain("Gården"));
        Assert.That(email.PlainText, Does.Contain("Frist overskredet"));
        Assert.That(email.PlainText, Does.Contain(
            "- Roundup Flex (reg.nr. 1-234) – Kemirum, Hylde 2 – brugs- og besiddelsesfrist overskredet 01-09-2026 – skal bortskaffes som farligt affald"));
        Assert.That(email.Html, Does.Contain("<h3 style=\"font-size:15px\">Frist overskredet</h3>"));
        Assert.That(email.Html, Does.Contain(WebUtility.HtmlEncode(ChemicalDigestEmailRenderer.Line(PassedPossession()))));
    }

    [Test]
    public void Render_LinksToW1WithTheRequiresActionFilter()
    {
        var email = ChemicalDigestEmailRenderer.Render(Digest(PassedPossession()), "4711", Monday);
        const string url = "https://4711.microting.com/plugins/backend-configuration-pn/chemicals?propertyId=10&filter=requires-action";

        Assert.That(ChemicalDigestEmailRenderer.W1Url("4711", 10), Is.EqualTo(url));
        Assert.That(email.Html, Does.Contain("href=\"" + WebUtility.HtmlEncode(url) + "\""));
        Assert.That(email.PlainText, Does.Contain(url));
    }

    [Test]
    public void Render_EscapesHtmlInNames()
    {
        var email = ChemicalDigestEmailRenderer.Render(
            Digest(PassedPossession(name: "<b>Gift & Co</b>", note: "<script>x</script>")), "4711", Monday);

        Assert.That(email.Html, Does.Not.Contain("<b>Gift"));
        Assert.That(email.Html, Does.Not.Contain("<script>"));
        Assert.That(email.Html, Does.Contain("&lt;b&gt;Gift &amp; Co&lt;/b&gt;"));
    }

    [TestCase(ChemicalAlertThresholdEnum.SixMonths, ChemicalDeadlineKindEnum.Sales, null, " – salgsfrist 01-09-2026")]
    [TestCase(ChemicalAlertThresholdEnum.OneMonth, ChemicalDeadlineKindEnum.UseAndPossession, null, " – brugs- og besiddelsesfrist 01-09-2026")]
    [TestCase(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales, null, " – salgsfrist overskredet 01-09-2026")]
    [TestCase(ChemicalAlertThresholdEnum.StatusChanged, null, 6, " – afmeldt")]
    [TestCase(ChemicalAlertThresholdEnum.StatusChanged, null, 7, " – udløbet")]
    [TestCase(null, null, null, "")]
    public void Line_DescribesTheItem(ChemicalAlertThresholdEnum? threshold, ChemicalDeadlineKindEnum? kind, int? status, string suffix)
    {
        var item = new ChemicalDigestItem(100, "Roundup Flex", "1-234", "Kemirum", "", threshold, kind, status,
            kind == null ? null : new DateOnly(2026, 9, 1));

        Assert.That(ChemicalDigestEmailRenderer.Line(item), Is.EqualTo("Roundup Flex (reg.nr. 1-234) – Kemirum" + suffix));
    }
}
