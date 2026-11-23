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

// Own namespace on purpose: needs no database, so it runs without the LiveFeed SetUpFixture.
namespace ServiceBackendConfigurationPlugin.Integration.Test.LiveFeedTimeout;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using TaskChangeKinds = Microting.EformBackendConfigurationBase.Infrastructure.Const.TaskChangeKinds;

[TestFixture]
public class TaskChangeFeedWriterTimeoutTests
{
    /// <summary>
    /// A "database" that accepts the TCP connection (the kernel completes the handshake from the
    /// listen backlog) but never sends the MySQL greeting: the stalled-server case. The writer's
    /// whole call, including the server-version detection and the connection open, must stay
    /// inside its 5 s budget and must not throw.
    /// </summary>
    [Test]
    public async Task Append_DatabaseNeverAnswers_ReturnsWithinTheBudget_AndDoesNotThrow()
    {
        using var blackHole = new TcpListener(IPAddress.Loopback, 0);
        blackHole.Start();
        var port = ((IPEndPoint)blackHole.LocalEndpoint).Port;
        var warnings = new List<string>();
        var writer = new TaskChangeFeedWriter(
            $"Server = 127.0.0.1; port = {port}; Database = never-answers; user = root; password = secretpassword; Convert Zero Datetime = true;",
            warnings.Add, _ => { });
        var hint = new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 1, AreaRulePlanningId: 2);

        var stopwatch = Stopwatch.StartNew();
        await Assert.DoesNotThrowAsync(() => writer.AppendAsync("UnitTest", hint));
        stopwatch.Stop();

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6.5)),
            "a stalled database must not hold up the job or handler beyond the writer's budget");
        Assert.That(warnings, Has.Count.EqualTo(1));
    }
}
