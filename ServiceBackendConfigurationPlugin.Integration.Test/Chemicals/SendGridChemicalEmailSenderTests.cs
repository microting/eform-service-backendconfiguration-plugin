namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using SendGrid;
using SendGrid.Helpers.Mail;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

[TestFixture]
public class SendGridChemicalEmailSenderTests
{
    private static readonly ChemicalDigestEmail Email =
        new("a@example.com", "Kemi: ugentlig oversigt uge 44", "<p>html</p>", "text");

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("SG.key", true)]
    public void IsConfigured_FollowsTheTenantKey(string key, bool expected)
    {
        var sender = new SendGridChemicalEmailSender(() => key, (_, _) => throw new AssertionException("must not send"));

        Assert.That(sender.IsConfigured(), Is.EqualTo(expected));
    }

    [Test]
    public async Task SendAsync_UsesTheKeyAndTheDigestFields()
    {
        string usedKey = null;
        SendGridMessage sent = null;
        var sender = new SendGridChemicalEmailSender(() => "SG.key", (key, message) =>
        {
            usedKey = key;
            sent = message;
            return Task.FromResult(new Response(HttpStatusCode.Accepted, null, null));
        });

        await sender.SendAsync(Email);

        Assert.That(usedKey, Is.EqualTo("SG.key"));
        var json = sent.Serialize();
        Assert.That(json, Does.Contain(SendGridChemicalEmailSender.FromAddress));
        Assert.That(json, Does.Contain(SendGridChemicalEmailSender.FromName));
        Assert.That(json, Does.Contain("a@example.com"));
        Assert.That(json, Does.Contain("Kemi: ugentlig oversigt uge 44"));
    }

    [Test]
    public void SendAsync_NonSuccessResponse_Throws()
    {
        var sender = new SendGridChemicalEmailSender(() => "SG.key",
            (_, _) => Task.FromResult(new Response(HttpStatusCode.InternalServerError, null, null)));

        Assert.That(() => sender.SendAsync(Email), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void SendAsync_WithoutKey_ThrowsWithoutSending()
    {
        var sender = new SendGridChemicalEmailSender(() => null, (_, _) => throw new AssertionException("must not send"));

        Assert.That(() => sender.SendAsync(Email), Throws.InstanceOf<InvalidOperationException>());
    }
}
