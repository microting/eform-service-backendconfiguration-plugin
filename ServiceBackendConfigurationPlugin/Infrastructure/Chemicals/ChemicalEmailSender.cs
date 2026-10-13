using System;
using System.Net;
using System.Threading.Tasks;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

public interface IChemicalEmailSender
{
    /// <summary>The tenant's key, or null/empty when none is set. The job reads it once per run.</summary>
    string ReadKey();

    /// <summary>Throws when the e-mail was not accepted; <see cref="ChemicalEmailAuthException"/> when the key is rejected.</summary>
    Task SendAsync(string key, ChemicalDigestEmail email);
}

/// <summary>SendGrid answered 401/403: systemic, so retrying other recipients is pointless.</summary>
public sealed class ChemicalEmailAuthException(string message) : InvalidOperationException(message);

/// <summary>
/// SendGrid with the tenant's EmailSettings:SendGridKey, the same key and
/// sender address SearchListJob's e-mails use.
/// </summary>
public sealed class SendGridChemicalEmailSender(
    Func<string> readKey, Func<string, SendGridMessage, Task<Response>> send) : IChemicalEmailSender
{
    public const string SendGridKeyId = "EmailSettings:SendGridKey";
    public const string FromAddress = "no-reply@microting.com";
    public const string FromName = "Microting Kemi";

    public string ReadKey() => readKey();

    public async Task SendAsync(string key, ChemicalDigestEmail email)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException($"{SendGridKeyId} is not set.");
        }

        var response = await send(key, BuildMessage(email));
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ChemicalEmailAuthException($"SendGrid answered {(int)response.StatusCode} for a chemical digest.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"SendGrid answered {(int)response.StatusCode} for a chemical digest.");
        }
    }

    public static SendGridMessage BuildMessage(ChemicalDigestEmail email) =>
        MailHelper.CreateSingleEmail(
            new EmailAddress(FromAddress, FromName), new EmailAddress(email.To), email.Subject, email.PlainText, email.Html);

    /// <summary>A one-line pass-through to the SDK; CI never talks to live SendGrid (C-14).</summary>
    public static Task<Response> SendWithSendGridAsync(string key, SendGridMessage message) =>
        new SendGridClient(key).SendEmailAsync(message);
}
