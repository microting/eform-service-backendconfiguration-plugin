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
namespace ServiceBackendConfigurationPlugin.Integration.Test.TaskReminders;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin.Messaging;
using ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

/// <summary>Records every send; answers with <see cref="Respond"/> (default: every message accepted).</summary>
public sealed class FakeTaskReminderSender : ITaskReminderSender
{
    private readonly object _gate = new();

    public bool Ready { get; set; } = true;

    public bool IsReady => Ready;

    public List<string> Initializations { get; } = [];

    public List<List<Message>> Calls { get; } = [];

    public Func<IReadOnlyList<Message>, IReadOnlyList<TaskReminderSendOutcome>> Respond { get; set; } =
        messages => messages.Select(_ => new TaskReminderSendOutcome(true, null)).ToList();

    /// <summary>Runs before answering; a test can block a send here.</summary>
    public Func<CancellationToken, Task> BeforeSend { get; set; } = _ => Task.CompletedTask;

    public void Initialize(string serviceAccountJson)
    {
        Initializations.Add(serviceAccountJson);
        if (!serviceAccountJson.StartsWith("{", StringComparison.Ordinal))
        {
            throw new FormatException("not a service-account key");
        }

        Ready = true;
    }

    public async Task<IReadOnlyList<TaskReminderSendOutcome>> SendEachAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Calls.Add(messages.ToList());
        }

        await BeforeSend(cancellationToken);
        return Respond(messages);
    }

    public List<Message> AllMessages()
    {
        lock (_gate)
        {
            return Calls.SelectMany(x => x).ToList();
        }
    }
}
