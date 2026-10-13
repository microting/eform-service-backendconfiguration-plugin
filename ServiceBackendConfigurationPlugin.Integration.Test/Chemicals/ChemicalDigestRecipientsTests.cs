namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

[TestFixture]
public class ChemicalDigestRecipientsTests
{
    [TestCase("a@example.com", new[] { "a@example.com" })]
    [TestCase("a@example.com,b@example.com", new[] { "a@example.com", "b@example.com" })]
    [TestCase("a@example.com;b@example.com", new[] { "a@example.com", "b@example.com" })]
    [TestCase(" A@EXAMPLE.com; ,bad, a@example.com", new[] { "a@example.com" })]
    [TestCase("Anna <a@example.com>", new string[0])]
    [TestCase("", new string[0])]
    [TestCase("   ", new string[0])]
    [TestCase(null, new string[0])]
    public void Parse_NormalisesDeduplicatesAndDropsInvalid(string raw, string[] expected)
    {
        Assert.That(ChemicalDigestRecipients.Parse(raw), Is.EqualTo(expected));
    }
}
