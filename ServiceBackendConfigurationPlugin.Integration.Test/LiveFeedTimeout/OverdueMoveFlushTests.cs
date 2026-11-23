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

// No database: the shipped loop helper of the hour-9 overdue move, with the loop body as a delegate.
namespace ServiceBackendConfigurationPlugin.Integration.Test.LiveFeedTimeout;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;
using TaskChangeKinds = Microting.EformBackendConfigurationBase.Infrastructure.Const.TaskChangeKinds;

[TestFixture]
public class OverdueMoveFlushTests
{
    private static TaskChangeHint Hint(int complianceId) =>
        new(TaskChangeKinds.Changed, PropertyId: 1, ComplianceId: complianceId);

    [Test]
    public async Task EveryMoveSucceeds_HintsOfTheMovesAreAppendedOnce_SkippedItemsGiveNone()
    {
        var appends = new List<IReadOnlyCollection<TaskChangeHint>>();

        await SearchListJob.ForEachThenAppendAsync(
            new[] { 1, 2, 3 },
            id => Task.FromResult(id == 2 ? null : Hint(id)),
            hints =>
            {
                appends.Add(hints);
                return Task.CompletedTask;
            });

        Assert.That(appends, Has.Count.EqualTo(1));
        Assert.That(appends[0], Is.EqualTo(new[] { Hint(1), Hint(3) }));
    }

    [Test]
    public async Task ALaterMoveThrows_TheEarlierMovesAreStillAppendedOnce_AndTheExceptionPropagatesUnchanged()
    {
        var appends = new List<IReadOnlyCollection<TaskChangeHint>>();
        var failure = new InvalidOperationException("move 3 failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => SearchListJob.ForEachThenAppendAsync(
            new[] { 1, 2, 3, 4 },
            id => id == 3 ? throw failure : Task.FromResult(Hint(id)),
            hints =>
            {
                appends.Add(hints);
                return Task.CompletedTask;
            }));

        Assert.That(thrown, Is.SameAs(failure));
        Assert.That(appends, Has.Count.EqualTo(1));
        Assert.That(appends[0], Is.EqualTo(new[] { Hint(1), Hint(2) }), "the saved moves 1 and 2 are not lost; 4 never ran");
    }
}
