using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FirebaseAdmin.Messaging;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

public enum ChemicalPushOutcome
{
    Delivered,

    /// <summary>UNREGISTERED: the token is gone; soft-delete it.</summary>
    DeadToken,

    /// <summary>
    /// INVALID_ARGUMENT: the token OR the payload is bad. Never prune on it alone:
    /// a systemic payload fault would otherwise delete every token.
    /// </summary>
    InvalidArgument,

    /// <summary>Token minted by another Firebase project, or (if EVERY send says so) our credential is wrong.</summary>
    SenderIdMismatch,

    /// <summary>Anything else: retry on the next run.</summary>
    Transient
}

public interface IChemicalPushSender
{
    /// <summary>Sends every message; returns one outcome per message, in order.</summary>
    Task<IReadOnlyList<ChemicalPushOutcome>> SendAsync(string serviceAccountJson, IReadOnlyList<Message> messages);
}

public sealed class FirebaseChemicalPushSender : IChemicalPushSender
{
    /// <summary>The service's own FirebaseApp for the microting-chemistry project (Plan D Contract C2).</summary>
    public const string FirebaseAppName = "microting-chemistry";

    /// <summary>FCM rejects SendEach batches above 500 messages.</summary>
    public const int FcmBatchLimit = 500;

    public async Task<IReadOnlyList<ChemicalPushOutcome>> SendAsync(
        string serviceAccountJson, IReadOnlyList<Message> messages)
    {
        var outcomes = new List<ChemicalPushOutcome>(messages.Count);
        if (messages.Count == 0)
        {
            return outcomes;
        }

        var messaging = NamedFirebaseMessaging.Get(FirebaseAppName, serviceAccountJson);
        for (var offset = 0; offset < messages.Count; offset += FcmBatchLimit)
        {
            var batch = await messaging.SendEachAsync(messages.Skip(offset).Take(FcmBatchLimit).ToList());
            outcomes.AddRange(batch.Responses.Select(response =>
                response.IsSuccess ? ChemicalPushOutcome.Delivered : Classify(response.Exception?.MessagingErrorCode)));
        }

        return outcomes;
    }

    public static ChemicalPushOutcome Classify(MessagingErrorCode? code) => code switch
    {
        MessagingErrorCode.Unregistered => ChemicalPushOutcome.DeadToken,
        MessagingErrorCode.InvalidArgument => ChemicalPushOutcome.InvalidArgument,
        MessagingErrorCode.SenderIdMismatch => ChemicalPushOutcome.SenderIdMismatch,
        _ => ChemicalPushOutcome.Transient
    };
}
