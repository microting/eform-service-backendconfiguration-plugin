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
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using Microting.EformBackendConfigurationBase.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

/// <summary>
/// One delivery pass over the task reminder outbox (<see cref="TaskNotificationOutbox"/>; its class
/// doc is the state machine). The web host plans the rows; this sends them:
/// <list type="bullet">
/// <item>claim up to <see cref="MaxRowsPerPass"/> due rows with a <see cref="Lease"/>;</item>
/// <item>a reminder (Sending) is sent as a visible message to every opted-in eForm token of the row's
/// site, and never at or after its planned start (then it is Expired);</item>
/// <item>a withdraw (Withdrawing) is sent as a silent message to every live eForm token of the site, opted in or not (see SelectRecipientTokens), until 24 hours
/// after the planned start;</item>
/// <item>each claimed row gets exactly one Mark* call; a Mark* refused because the claim is stale
/// (the lease was reaped and another deliverer owns the row) is logged at debug and ignored.</item>
/// </list>
/// Two deliverers (e.g. two pods during a deploy) can run against the same rows: a claim is fenced
/// by its attempt number, so each claim is delivered by one deliverer only.
/// </summary>
public sealed class TaskReminderDeliverer
{
    /// <summary>DeviceToken.AppId of the eForm mobile app ("eform", as the web host's DeviceAppIds.Eform
    /// and the app's registration; nothing enforces that they agree).</summary>
    public const string EformAppId = "eform";

    public const int MaxRowsPerPass = 50;

    /// <summary>FCM rejects SendEach batches above 500 messages.</summary>
    public const int FcmBatchLimit = 500;

    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>A withdraw is retried until this long after the planned start, then given up.</summary>
    public static readonly TimeSpan WithdrawWindow = TimeSpan.FromHours(24);

    public static readonly TimeSpan MaxBackOff = TimeSpan.FromSeconds(60);

    /// <summary>The longest one FCM call may take.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<BackendConfigurationPnDbContext>> _createContext;
    private readonly ITaskReminderSender _sender;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _log;
    private readonly Dictionary<string, DateTime> _lastLogged = new();

    // Warnings that can repeat for every reminder and every retry are logged at most this often.
    private static readonly TimeSpan RepeatedWarningInterval = TimeSpan.FromHours(1);

    public TaskReminderDeliverer(
        Func<CancellationToken, Task<BackendConfigurationPnDbContext>> createContext,
        ITaskReminderSender sender,
        Func<DateTime> utcNow = null,
        Action<string> log = null)
    {
        _createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log ?? Console.WriteLine;
    }

    /// <summary>What one pass did, for logs and tests.</summary>
    public sealed record PassResult(
        int Claimed, int Sent, int Withdrawn, int Retried, int Expired, int Failed, int Stale, int Deferred, int TokensPruned);

    /// <summary>
    /// The live eForm tokens of one site; for a reminder only those that opted in
    /// (<c>TaskReminders</c>). A withdraw deliberately goes to every live eForm token, opted in or not:
    /// a phone that showed a reminder and then switched reminders off must still have it removed, and
    /// the flag cannot tell "opted out" from "old app"; a withdraw is a silent data message that old
    /// app versions ignore. The (AppId, SdkSiteId, WorkflowState) index serves it; AppId must stay in
    /// the predicate (see AdhocReminderJob.SelectRecipientTokens).
    /// </summary>
    public static IQueryable<DeviceToken> SelectRecipientTokens(
        IQueryable<DeviceToken> deviceTokens, int sdkSiteId, bool withdraw = false)
    {
        var tokens = deviceTokens
            .Where(x => x.AppId == EformAppId)
            .Where(x => x.SdkSiteId == sdkSiteId)
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created)
            .Where(x => x.FcmToken != null && x.FcmToken != "");
        return withdraw ? tokens : tokens.Where(x => x.TaskReminders);
    }

    /// <summary>Back-off after the n-th attempt: 2, 4, 8, 16, 32, then 60 seconds.</summary>
    public static TimeSpan BackOff(int attempts) =>
        attempts >= 6 ? MaxBackOff : TimeSpan.FromSeconds(Math.Min(MaxBackOff.TotalSeconds, Math.Pow(2, Math.Max(1, attempts))));

    /// <summary>An FCM error code as stored in LastError: kebab case ("unregistered", "sender-id-mismatch").</summary>
    public static string ErrorCode(MessagingErrorCode? code)
    {
        if (code == null)
        {
            return "send-error";
        }

        var name = code.Value.ToString();
        var result = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(name[i]));
        }

        return result.ToString();
    }

    /// <summary>Returns expired leases to their queue (a deliverer died or hung). Returns the count.</summary>
    public async Task<int> ReapAsync(CancellationToken cancellationToken)
    {
        await using var db = await _createContext(cancellationToken).ConfigureAwait(false);
        return await TaskNotificationOutbox.ReapExpiredLeasesAsync(db, _utcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims and delivers due rows. Rows still unhandled when <paramref name="budget"/> is spent are
    /// handed back at once (due again on the next pass) instead of waiting for their lease to end.
    /// The sender must be ready.
    /// </summary>
    public async Task<PassResult> RunOnceAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        await using var db = await _createContext(cancellationToken).ConfigureAwait(false);
        var claimed = await TaskNotificationOutbox
            .ClaimDueAsync(db, _utcNow(), MaxRowsPerPass, Lease, cancellationToken)
            .ConfigureAwait(false);

        var tally = new Tally();
        foreach (var row in claimed)
        {
            if (elapsed.Elapsed > budget)
            {
                tally.Deferred++;
                await MarkAsync(row, "deferred", TaskNotificationOutbox.MarkRetryAsync(
                    db, row, _utcNow(), "deferred", _utcNow(), cancellationToken), tally, _ => { }).ConfigureAwait(false);
                continue;
            }

            await DeliverAsync(db, row, tally, cancellationToken).ConfigureAwait(false);
        }

        return new PassResult(claimed.Count, tally.Sent, tally.Withdrawn, tally.Retried, tally.Expired, tally.Failed,
            tally.Stale, tally.Deferred, tally.Pruned);
    }

    private sealed class Tally
    {
        public int Sent, Withdrawn, Retried, Expired, Failed, Stale, Deferred, Pruned;
    }

    private async Task DeliverAsync(BackendConfigurationPnDbContext db, TaskNotification row, Tally tally, CancellationToken cancellationToken)
    {
        var withdraw = row.State == TaskNotificationState.Withdrawing;
        var now = _utcNow();
        var giveUpAt = withdraw ? row.PlannedStartUtc + WithdrawWindow : row.PlannedStartUtc;

        if (now >= giveUpAt)
        {
            // Never send a reminder at or after the start; a withdraw this late is pointless.
            await GiveUpAsync(db, row, withdraw, now, tally, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tokens = await SelectRecipientTokens(db.DeviceTokens, row.SdkSiteId, withdraw)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var delivered = 0;
        var pruned = 0;
        string transientCode = null;
        string permanentCode = null;

        for (var offset = 0; offset < tokens.Count; offset += FcmBatchLimit)
        {
            var chunk = tokens.Skip(offset).Take(FcmBatchLimit).ToList();
            var messages = chunk
                .Select(t => withdraw ? TaskReminderMessages.TaskWithdrawn(row, t.FcmToken) : TaskReminderMessages.TaskDue(row, t.FcmToken))
                .ToList();

            IReadOnlyList<TaskReminderSendOutcome> outcomes;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(SendTimeout);
                try
                {
                    outcomes = await _sender.SendEachAsync(messages, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    transientCode ??= "timeout";
                    continue;
                }
                catch (FirebaseMessagingException e)
                {
                    // The whole call failed (e.g. authentication, network): nothing is known about the tokens.
                    transientCode ??= e.MessagingErrorCode is { } code ? ErrorCode(code) : "send-error";
                    continue;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    transientCode ??= "send-error";
                    continue;
                }
            }

            // A success in the batch proves the credential is the right project; only then is a
            // SenderIdMismatch about the token (a foreign one). Without a success it may be a key for the
            // wrong Firebase project, which mismatches EVERY real token: prune none of them, retry.
            // (Stricter than AdhocReminderJob.IsCredentialFaultBatch, which requires every response to
            // mismatch, so one other answer in a wrong-project batch would let it prune real tokens.)
            var credentialProven = outcomes.Any(x => x.Success);
            var unprovenMismatches = 0;

            for (var i = 0; i < outcomes.Count && i < chunk.Count; i++)
            {
                var outcome = outcomes[i];
                if (outcome.Success)
                {
                    delivered++;
                    continue;
                }

                switch (outcome.ErrorCode)
                {
                    case MessagingErrorCode.InvalidArgument:
                        // Read as a fault in OUR message (e.g. APNs headers Apple rejects), never as a dead
                        // token: pruning here could remove every iOS token of a site whose Android phones
                        // accept. A permanent failure for this token; the token is kept.
                        permanentCode ??= ErrorCode(outcome.ErrorCode);
                        LogThrottled("invalid-argument",
                            $"warn: TaskReminderDeliverer: row {row.Id} site {row.SdkSiteId}: a device answered invalid-argument; treated as a fault in the message, token kept (logged at most once an hour)");
                        break;
                    case MessagingErrorCode.SenderIdMismatch when !credentialProven:
                        unprovenMismatches++;
                        break;
                    case MessagingErrorCode.Unregistered:
                    case MessagingErrorCode.SenderIdMismatch:
                        if (await PruneAsync(db, chunk[i], row, outcome.ErrorCode.Value, cancellationToken).ConfigureAwait(false))
                        {
                            pruned++;
                        }

                        permanentCode ??= ErrorCode(outcome.ErrorCode);
                        break;
                    case MessagingErrorCode.ThirdPartyAuthError:
                        // The APNs or web-push credential of the Firebase project; the token is fine.
                        permanentCode ??= ErrorCode(outcome.ErrorCode);
                        break;
                    default:
                        // Unavailable, Internal, QuotaExceeded, or no code: try again.
                        transientCode ??= ErrorCode(outcome.ErrorCode);
                        break;
                }
            }

            if (unprovenMismatches > 0)
            {
                // Possibly a credential fault: the row retries under this code (it wins over others).
                transientCode = ErrorCode(MessagingErrorCode.SenderIdMismatch);
                LogThrottled("sender-id-mismatch",
                    $"warn: TaskReminderDeliverer: row {row.Id} site {row.SdkSiteId}: {unprovenMismatches} token(s) failed with sender-id-mismatch and none succeeded; possible credential fault, no such token pruned (logged at most once an hour)");
            }
        }

        tally.Pruned += pruned;
        now = _utcNow();
        if (delivered > 0 || tokens.Count == 0)
        {
            // Delivered to at least one phone, or nobody to tell (no opted-in token): done. Known limit:
            // a phone that failed transiently in the same call is not retried (one row per reminder,
            // not per phone).
            // A send that returned after the planned start is still marked Sent: it left before the
            // start check failed, and it is never sent again.
            if (withdraw)
            {
                await MarkAsync(row, "withdrawn", TaskNotificationOutbox.MarkWithdrawnAsync(db, row, now, cancellationToken),
                    tally, t => t.Withdrawn++).ConfigureAwait(false);
            }
            else
            {
                await MarkAsync(row, "sent", TaskNotificationOutbox.MarkSentAsync(db, row, now, cancellationToken),
                    tally, t => t.Sent++).ConfigureAwait(false);
            }

            if (tokens.Count > 0)
            {
                _log($"info: TaskReminderDeliverer: row {row.Id} site {row.SdkSiteId}: {(withdraw ? "withdraw" : "reminder")} delivered to {delivered} of {tokens.Count} device(s), {pruned} dead token(s) removed");
            }

            return;
        }

        if (transientCode != null)
        {
            var next = now + BackOff(row.Attempts);
            if (next >= giveUpAt)
            {
                await GiveUpAsync(db, row, withdraw, now, tally, cancellationToken).ConfigureAwait(false);
                return;
            }

            await MarkAsync(row, "retry", TaskNotificationOutbox.MarkRetryAsync(
                db, row, next, transientCode, now, cancellationToken), tally, t => t.Retried++).ConfigureAwait(false);
            _log($"info: TaskReminderDeliverer: row {row.Id} site {row.SdkSiteId}: {transientCode}, attempt {row.Attempts}, retry in {(next - now).TotalSeconds:0} s");
            return;
        }

        var failure = permanentCode ?? "send-error";
        await MarkAsync(row, "failed", TaskNotificationOutbox.MarkFailedAsync(db, row, failure, now, cancellationToken),
            tally, t => t.Failed++).ConfigureAwait(false);
        _log($"warn: TaskReminderDeliverer: row {row.Id} site {row.SdkSiteId}: failed ({failure}), {pruned} dead token(s) removed");
    }

    /// <summary>Too late: a reminder is Expired (never sent at or after the start), a withdraw Failed.</summary>
    private Task GiveUpAsync(
        BackendConfigurationPnDbContext db, TaskNotification row, bool withdraw, DateTime now, Tally tally,
        CancellationToken cancellationToken) =>
        withdraw
            ? MarkAsync(row, "failed", TaskNotificationOutbox.MarkFailedAsync(
                db, row, "withdraw-too-late", now, cancellationToken), tally, t => t.Failed++)
            : MarkAsync(row, "expired", TaskNotificationOutbox.MarkExpiredAsync(
                db, row, now, cancellationToken), tally, t => t.Expired++);

    /// <summary>Logs <paramref name="message"/> at most once per <see cref="RepeatedWarningInterval"/> per kind.</summary>
    private void LogThrottled(string kind, string message)
    {
        var now = _utcNow();
        lock (_lastLogged)
        {
            if (_lastLogged.TryGetValue(kind, out var last) && now - last < RepeatedWarningInterval)
            {
                return;
            }

            _lastLogged[kind] = now;
        }

        _log(message);
    }

    private async Task MarkAsync(TaskNotification row, string what, Task<bool> mark, Tally tally, Action<Tally> count)
    {
        if (await mark.ConfigureAwait(false))
        {
            count(tally);
            return;
        }

        // Fenced: the lease ended and another deliverer owns the row now. Not an error.
        tally.Stale++;
        _log($"dbug: TaskReminderDeliverer: row {row.Id}: {what} not applied, the claim is no longer this deliverer's");
    }

    /// <summary>Soft-deletes a token FCM says is dead or foreign (as AdhocReminderJob). Never throws.</summary>
    private async Task<bool> PruneAsync(
        BackendConfigurationPnDbContext db, DeviceToken token, TaskNotification row, MessagingErrorCode code, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await token.Delete(db).ConfigureAwait(false);
            _log($"info: TaskReminderDeliverer: row {row.Id} site {row.SdkSiteId}: device token {token.Id} removed ({ErrorCode(code)})");
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            _log($"warn: TaskReminderDeliverer: row {row.Id} site {row.SdkSiteId}: device token {token.Id} could not be removed ({e.GetType().Name})");
            return false;
        }
    }
}
