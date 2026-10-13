namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FirebaseAdmin.Messaging;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>The only clock the chemical job tests use (no DateTime.UtcNow, no sleeps).</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class FakeChemicalPushSender : IChemicalPushSender
{
    public List<(string ServiceAccountJson, Message Message)> Sent { get; } = [];

    public Func<Message, ChemicalPushOutcome> Outcome { get; set; } = _ => ChemicalPushOutcome.Delivered;

    public Task<IReadOnlyList<ChemicalPushOutcome>> SendAsync(string serviceAccountJson, IReadOnlyList<Message> messages)
    {
        Sent.AddRange(messages.Select(m => (serviceAccountJson, m)));
        return Task.FromResult<IReadOnlyList<ChemicalPushOutcome>>(messages.Select(Outcome).ToList());
    }
}

public sealed class FakeChemicalSdkDirectory : IChemicalSdkDirectory
{
    public Dictionary<int, string> Languages { get; } = [];

    public string CustomerNo { get; set; } = "4711";

    /// <summary>The real directory throws when the SDK setting is missing.</summary>
    public bool CustomerNoThrows { get; set; }

    public int CustomerNoReads { get; private set; }

    public Task<IReadOnlyDictionary<int, string>> LanguageCodesAsync(IReadOnlyCollection<int> sdkSiteIds) =>
        Task.FromResult<IReadOnlyDictionary<int, string>>(
            Languages.Where(x => sdkSiteIds.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value));

    public Task<string> CustomerNoAsync()
    {
        CustomerNoReads++;
        return CustomerNoThrows
            ? Task.FromException<string>(new InvalidOperationException("customerNo setting missing"))
            : Task.FromResult(CustomerNo);
    }
}

public sealed class FakeChemicalEmailSender : IChemicalEmailSender
{
    public string Key { get; set; } = "SG.test";

    public int KeyReads { get; private set; }

    /// <summary>Every attempted e-mail, including those that failed.</summary>
    public List<ChemicalDigestEmail> Attempted { get; } = [];

    /// <summary>The key each attempt was made with.</summary>
    public List<string> KeysUsed { get; } = [];

    public Func<ChemicalDigestEmail, bool> Fails { get; set; } = _ => false;

    /// <summary>A 401/403-style systemic failure.</summary>
    public Func<ChemicalDigestEmail, bool> AuthFails { get; set; } = _ => false;

    /// <summary>An HttpClient timeout to SendGrid (TaskCanceledException): an ordinary send failure.</summary>
    public Func<ChemicalDigestEmail, bool> TimesOut { get; set; } = _ => false;

    /// <summary>Runs before each send, e.g. to observe the database at that moment.</summary>
    public Func<ChemicalDigestEmail, Task> BeforeSend { get; set; } = _ => Task.CompletedTask;

    public string ReadKey()
    {
        KeyReads++;
        return Key;
    }

    public async Task SendAsync(string key, ChemicalDigestEmail email)
    {
        Attempted.Add(email);
        KeysUsed.Add(key);
        await BeforeSend(email);
        if (TimesOut(email))
        {
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        }

        if (AuthFails(email))
        {
            throw new ChemicalEmailAuthException("SendGrid answered 401 for a chemical digest.");
        }

        if (Fails(email))
        {
            throw new InvalidOperationException("SendGrid answered 500 for a chemical digest.");
        }
    }
}
