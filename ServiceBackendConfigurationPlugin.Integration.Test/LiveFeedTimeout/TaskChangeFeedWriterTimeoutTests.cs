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
using System.Threading;
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

    /// <summary>
    /// The hard cutoff, tested at the context seam: a write that blocks and ignores cancellation (a
    /// server that authenticated and then stopped answering, where MySqlConnector's cancellation
    /// grace period lets a command outlive its timeout). A fake MySQL server that completes the
    /// handshake and authentication is too heavy for a robust small test. The caller must get control
    /// back at the budget; while the abandoned write still runs, further calls are skipped at once;
    /// once it has ended, writes are tried again.
    /// </summary>
    [Test]
    public async Task Append_WriteIgnoresCancellation_CallerReturnsAtTheBudget_NextCallSkippedUntilItEnds()
    {
        using var release = new ManualResetEventSlim();
        var attempts = 0;
        var warnings = new List<string>();
        var now = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        var writer = new TaskChangeFeedWriter(
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    release.Wait(TimeSpan.FromSeconds(30)); // blocks and ignores every token
                }

                throw new InvalidOperationException("no database in this test");
            },
            warnings.Add, _ => { }, () => now);
        var hint = new TaskChangeHint(TaskChangeKinds.Changed, PropertyId: 1, AreaRulePlanningId: 2);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await Assert.DoesNotThrowAsync(() => writer.AppendAsync("UnitTest", hint));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6.5)), "the caller gets control back at the budget");
            Assert.That(warnings, Has.Count.EqualTo(1), "the abandoned write is reported through the throttled warning");

            stopwatch.Restart();
            await Assert.DoesNotThrowAsync(() => writer.AppendAsync("UnitTest", hint));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)), "skipped at once, not queued behind the stuck write");
            Assert.That(Volatile.Read(ref attempts), Is.EqualTo(1), "no second write while the first one still runs");
        }
        finally
        {
            release.Set();
        }

        // The abandoned write ends (its factory throws); after that a call is tried again.
        var retried = Stopwatch.StartNew();
        while (Volatile.Read(ref attempts) < 2 && retried.Elapsed < TimeSpan.FromSeconds(10))
        {
            await writer.AppendAsync("UnitTest", hint);
            await Task.Delay(50);
        }

        Assert.That(Volatile.Read(ref attempts), Is.EqualTo(2), "writes resume once the abandoned one has ended");
    }
}
