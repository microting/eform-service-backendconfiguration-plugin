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


using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure.Data.Entities;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

public sealed record ChemicalSyncDecision(bool ApplyUpserts, bool ApplyRemovals, string Reason);

// RED stub: signatures only. ProcessFeedAsync mirrors the pre-fix SearchListJob
// collection (unsynchronised List.Add inside Parallel.ForEachAsync).
public static class ChemicalFeedGuard
{
    public const double MinimumFeedToLocalRatio = 0;
    public const int RatioCheckMinimumLocalCount = 0;

    public static ChemicalSyncDecision Decide(HttpStatusCode statusCode, int? feedCount, int localActiveCount)
        => throw new NotImplementedException();

    public static List<int> SelectIdsToRemove(
        IReadOnlySet<string> feedRegistrationNos, IEnumerable<(int Id, string RegistrationNo)> activeLocals)
        => throw new NotImplementedException();

    public static bool TryRestore(Chemical local, Chemical feedChemical)
        => throw new NotImplementedException();

    public static async Task<IReadOnlySet<string>> ProcessFeedAsync(
        IEnumerable<Chemical> feed,
        Func<Chemical, CancellationToken, ValueTask> processOne,
        ParallelOptions parallelOptions)
    {
        var regNos = new List<string>();
        await Parallel.ForEachAsync(feed, parallelOptions, async (chemical, ct) =>
        {
            regNos.Add(chemical.RegistrationNo);
            await processOne(chemical, ct);
        });
        return new HashSet<string>(regNos);
    }
}
