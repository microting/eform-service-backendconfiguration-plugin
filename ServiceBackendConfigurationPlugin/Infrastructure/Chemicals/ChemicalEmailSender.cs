using System;
using System.Threading.Tasks;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

public interface IChemicalEmailSender
{
    bool IsConfigured();

    /// <summary>Throws when the e-mail was not accepted.</summary>
    Task SendAsync(ChemicalDigestEmail email);
}

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

    public bool IsConfigured() => !string.IsNullOrEmpty(readKey());

    public async Task SendAsync(ChemicalDigestEmail email)
    {
        var key = readKey();
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException($"{SendGridKeyId} is not set.");
        }

        var response = await send(key, BuildMessage(email));
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
