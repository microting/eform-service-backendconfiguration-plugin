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
    public bool Configured { get; set; } = true;

    /// <summary>Every attempted e-mail, including those that failed.</summary>
    public List<ChemicalDigestEmail> Attempted { get; } = [];

    public Func<ChemicalDigestEmail, bool> Fails { get; set; } = _ => false;

    public bool IsConfigured() => Configured;

    public Task SendAsync(ChemicalDigestEmail email)
    {
        Attempted.Add(email);
        return Fails(email)
            ? Task.FromException(new InvalidOperationException($"SendGrid answered 500 for {email.To}"))
            : Task.CompletedTask;
    }
}
