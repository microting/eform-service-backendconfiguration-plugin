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

    [TestCase(null)]
    [TestCase("SG.key")]
    public void ReadKey_ReturnsTheTenantKey(string key)
    {
        var sender = new SendGridChemicalEmailSender(() => key, (_, _) => throw new AssertionException("must not send"));

        Assert.That(sender.ReadKey(), Is.EqualTo(key));
    }

    [Test]
    public async Task SendAsync_UsesTheGivenKeyAndTheDigestFields()
    {
        string usedKey = null;
        SendGridMessage sent = null;
        var sender = new SendGridChemicalEmailSender(() => throw new AssertionException("the key is read once per run, by the job"),
            (key, message) =>
            {
                usedKey = key;
                sent = message;
                return Task.FromResult(new Response(HttpStatusCode.Accepted, null, null));
            });

        await sender.SendAsync("SG.key", Email);

        Assert.That(usedKey, Is.EqualTo("SG.key"));
        var json = sent.Serialize();
        Assert.That(json, Does.Contain(SendGridChemicalEmailSender.FromAddress));
        Assert.That(json, Does.Contain(SendGridChemicalEmailSender.FromName));
        Assert.That(json, Does.Contain("a@example.com"));
        Assert.That(json, Does.Contain("Kemi: ugentlig oversigt uge 44"));
    }

    [Test]
    public void SendAsync_ServerError_ThrowsAPlainFailure()
    {
        var sender = new SendGridChemicalEmailSender(() => "SG.key",
            (_, _) => Task.FromResult(new Response(HttpStatusCode.InternalServerError, null, null)));

        Assert.That(() => sender.SendAsync("SG.key", Email),
            Throws.InstanceOf<InvalidOperationException>().And.Not.InstanceOf<ChemicalEmailAuthException>());
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    public void SendAsync_UnauthorizedOrForbidden_ThrowsTheAuthFailure(HttpStatusCode status)
    {
        var sender = new SendGridChemicalEmailSender(() => "SG.key",
            (_, _) => Task.FromResult(new Response(status, null, null)));

        Assert.That(() => sender.SendAsync("SG.key", Email), Throws.InstanceOf<ChemicalEmailAuthException>());
    }

    [Test]
    public void SendAsync_WithoutKey_ThrowsWithoutSending()
    {
        var sender = new SendGridChemicalEmailSender(() => null, (_, _) => throw new AssertionException("must not send"));

        Assert.That(() => sender.SendAsync(null, Email), Throws.InstanceOf<InvalidOperationException>());
    }
}
