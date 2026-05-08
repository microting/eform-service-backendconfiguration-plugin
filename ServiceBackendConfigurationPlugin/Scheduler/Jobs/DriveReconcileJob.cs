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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Sentry;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>
/// Daily reconcile job — backstop for missed webhook deliveries.
///
/// Per spec PR-6, this job should:
///   1) For every active <c>GoogleOAuthToken</c>, get a Drive access token.
///   2) Call <c>changes.list?pageToken={savedStartPageToken}</c>.
///   3) For each change matching a tracked <c>DriveFileId</c>, enqueue a
///      refresh through PR-7's processor.
///
/// Two pieces are still missing for the actual call:
///   - The base schema (10.0.33) does NOT yet carry a
///     <c>StartPageToken</c> column on <c>GoogleOAuthToken</c>; without it
///     <c>changes.list</c> has no anchor and would have to scan the entire
///     change feed every day.
///   - PR-7's <c>GoogleDriveChangeProcessor</c> service does not exist yet
///     in this process; the cron has nowhere to dispatch matched changes.
///
/// PR-6 ships the SCAFFOLDING (job class, IoC registration, daily timer)
/// and is wired into <c>Core.ConfigureScheduler</c> alongside the renewal
/// and keep-alive jobs. The actual changes-feed walk lands in PR-7 once
/// (a) the base bumps to add <c>StartPageToken</c>, and (b) a cross-process
/// path to the change processor exists (most likely a Rebus message that
/// the plugin process consumes — the plugin has the S3/UploadedData
/// pipeline wired up; the service does not).
///
/// In the meantime the Execute() body counts the candidate set so we can
/// see in the logs that the scaffolding is firing, which is also what the
/// PR-7 implementation will iterate over.
/// </summary>
public class DriveReconcileJob : IJob
{
    private readonly BackendConfigurationDbContextHelper _dbContextHelper;

    public DriveReconcileJob(BackendConfigurationDbContextHelper dbContextHelper)
    {
        _dbContextHelper = dbContextHelper;
    }

    public async Task Execute()
    {
        try
        {
            await using var db = _dbContextHelper.GetDbContext();
            var settings = DriveServiceSettings.Resolve(db);
            if (!settings.IsConfigured)
            {
                Console.WriteLine("info: DriveReconcileJob - GoogleDrive config missing, skipping");
                return;
            }

            // Candidate set: every token that's still active. The actual
            // PR-7 implementation walks one Drive changes feed per token and
            // dispatches matched files to the change processor. We
            // enumerate here so the daily run produces a visible signal in
            // the logs that the scaffolding fires.
            var activeTokens = await db.GoogleOAuthTokens
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.RevokedAt == null)
                .CountAsync().ConfigureAwait(false);

            // Tracked-file set: AreaRulePlanningFile rows backed by Drive
            // (i.e. DriveFileId is non-null). These are the rows PR-7 will
            // refresh when changes.list reports a hit. Grouping by
            // GoogleOAuthTokenId mirrors the per-token loop the actual
            // implementation will use.
            var trackedByToken = await db.AreaRulePlanningFiles
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.DriveFileId != null)
                .Where(x => x.GoogleOAuthTokenId != null)
                .GroupBy(x => x.GoogleOAuthTokenId)
                .Select(g => new { TokenId = g.Key, Count = g.Count() })
                .ToListAsync().ConfigureAwait(false);

            var trackedTotal = trackedByToken.Sum(x => x.Count);

            Console.WriteLine(
                $"info: DriveReconcileJob - reconcile not yet wired (pending base v10.0.34 with StartPageToken column and PR-7 change processor); "
                + $"would have processed {activeTokens} active token(s) covering {trackedTotal} tracked file(s) across {trackedByToken.Count} token group(s)");

            // TODO PR-7:
            //   foreach token in activeTokens:
            //     accessToken = await GetAccessTokenAsync(token)
            //     pageToken   = token.StartPageToken     // NEW column, base v10.0.34
            //     loop:
            //       resp = GET drive/v3/changes?pageToken=...&fields=changes,nextPageToken,newStartPageToken
            //       for each change in resp.changes:
            //           if change.fileId in trackedByToken[token.Id]:
            //               // hand off to PR-7's GoogleDriveChangeProcessor
            //               // (cross-process: most likely a Rebus message
            //               //  the plugin consumes, since the plugin owns
            //               //  the S3/UploadedData refresh pipeline)
            //       if resp.nextPageToken: pageToken = resp.nextPageToken; continue
            //       if resp.newStartPageToken:
            //           token.StartPageToken = resp.newStartPageToken
            //           await token.Update(db)
            //           break
        }
        catch (Exception ex)
        {
            Console.WriteLine($"fail: DriveReconcileJob - {ex.Message}");
            Console.WriteLine($"fail: {ex.StackTrace}");
            SentrySdk.CaptureException(ex);
        }
    }
}
