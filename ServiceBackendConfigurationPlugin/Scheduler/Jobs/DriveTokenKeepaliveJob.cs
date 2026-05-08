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
/// Logically monthly job — refreshes Google OAuth tokens that have not been
/// touched in 30+ days. Google's OAuth contract revokes refresh tokens
/// after 6 months of inactivity, so the keep-alive guards against that.
///
/// We run this from the SAME daily timer as the channel-renewal and
/// reconcile jobs (per the spec "the safe approach is to run it daily and
/// let the job filter by LastUsedAt < now-30d itself"). On most days it's a
/// no-op because no token will hit the 30-day threshold; on the days it
/// does, the per-token cost is one HTTP round-trip to the proxy.
///
/// Spec: PR-6, "Monthly token keep-alive".
/// </summary>
public class DriveTokenKeepaliveJob : IJob
{
    private readonly BackendConfigurationDbContextHelper _dbContextHelper;

    public DriveTokenKeepaliveJob(BackendConfigurationDbContextHelper dbContextHelper)
    {
        _dbContextHelper = dbContextHelper;
    }

    public async Task Execute()
    {
        await using var db = _dbContextHelper.GetDbContext();
        var settings = DriveServiceSettings.Resolve(db);
        if (!settings.IsConfigured)
        {
            Console.WriteLine("info: DriveTokenKeepaliveJob - GoogleDrive config missing, skipping");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var stale = await db.GoogleOAuthTokens
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.RevokedAt == null)
            .Where(x => x.LastUsedAt == null || x.LastUsedAt < cutoff)
            .OrderBy(x => x.LastUsedAt)
            .ToListAsync().ConfigureAwait(false);

        Console.WriteLine($"info: DriveTokenKeepaliveJob - {stale.Count} token(s) to keep alive");

        if (stale.Count == 0)
        {
            return;
        }

        using var oauthProxy = new OAuthProxyClient(settings.MicrotingOAuthProxyUrl, settings.ProxySigningKey);
        var touched = 0;
        var revoked = 0;

        foreach (var token in stale)
        {
            try
            {
                var refreshToken = RefreshTokenCrypto.Decrypt(
                    token.EncryptedRefreshToken, settings.RefreshTokenEncryptionKey);

                try
                {
                    await oauthProxy.RefreshAsync(refreshToken).ConfigureAwait(false);
                    token.LastUsedAt = DateTime.UtcNow;
                    await token.Update(db).ConfigureAwait(false);
                    touched++;
                }
                catch (OAuthProxyClient.TokenRevokedException ex)
                {
                    Console.WriteLine(
                        $"warn: DriveTokenKeepaliveJob - token revoked for user {token.UserId}: {ex.Message}");
                    token.RevokedAt = DateTime.UtcNow;
                    await token.Update(db).ConfigureAwait(false);
                    revoked++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"fail: DriveTokenKeepaliveJob - token {token.Id}: {ex.Message}");
                Console.WriteLine($"fail: {ex.StackTrace}");
                SentrySdk.CaptureException(ex);
                // Continue with the rest — one bad row shouldn't sink the
                // whole pass.
            }
        }

        Console.WriteLine(
            $"info: DriveTokenKeepaliveJob - touched {touched}, revoked {revoked}, of {stale.Count} stale token(s)");
    }
}
