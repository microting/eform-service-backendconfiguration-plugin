/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin.Messaging;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

/// <summary>FCM's answer for one message of a send: accepted, or the error code (null when FCM gave none).</summary>
public sealed record TaskReminderSendOutcome(bool Success, MessagingErrorCode? ErrorCode);

/// <summary>
/// The push seam of the task reminder loop. The real implementation is
/// <see cref="FirebaseTaskReminderSender"/>; tests use a fake.
/// </summary>
public interface ITaskReminderSender
{
    /// <summary>True once a credential has been applied.</summary>
    bool IsReady { get; }

    /// <summary>Applies the eForm app's service-account JSON. Throws when it is not a valid
    /// service-account key; the sender then stays not ready.</summary>
    void Initialize(string serviceAccountJson);

    /// <summary>One FCM call for up to 500 messages; one outcome per message, in order. A failure of the
    /// whole call (network, timeout, auth) is thrown.</summary>
    Task<IReadOnlyList<TaskReminderSendOutcome>> SendEachAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken);
}

/// <summary>
/// Sends through a NAMED FirebaseApp for the eForm mobile app, built like the service's other
/// senders (<see cref="NamedFirebaseMessaging"/>; see AdhocReminderJob.EnsureAdhocMessaging for why
/// the default app must never be used). The web host uses the same name in its own process.
/// Like the other named apps, the app is cached for the life of the process: rotating the credential
/// needs a service restart.
/// </summary>
public sealed class FirebaseTaskReminderSender : ITaskReminderSender
{
    public const string FirebaseAppName = "microting-eform";

    private FirebaseMessaging _messaging;

    public bool IsReady => Volatile.Read(ref _messaging) != null;

    public void Initialize(string serviceAccountJson) =>
        Volatile.Write(ref _messaging, NamedFirebaseMessaging.Get(FirebaseAppName, serviceAccountJson));

    public async Task<IReadOnlyList<TaskReminderSendOutcome>> SendEachAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var batch = await Volatile.Read(ref _messaging).SendEachAsync(messages, cancellationToken).ConfigureAwait(false);
        return batch.Responses
            .Select(x => new TaskReminderSendOutcome(x.IsSuccess, x.IsSuccess ? null : x.Exception?.MessagingErrorCode))
            .ToList();
    }
}
