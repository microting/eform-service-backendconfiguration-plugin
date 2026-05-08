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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Sentry;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

namespace ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>
/// Daily job — renews Drive watch channels that are within 24h of expiring.
///
/// For each soon-to-expire <c>DriveWatchChannel</c>:
///   1) Acquire an access token for the channel's user (via the OAuth proxy
///      <c>/refresh</c> endpoint, decrypting the persisted refresh token).
///   2) <c>POST drive/v3/changes/watch</c> with a freshly minted channelId
///      and channel-token JWT.
///   3) Persist the new <c>DriveWatchChannel</c> row, then soft-delete the
///      old one.
///   4) Best-effort <c>POST drive/v3/channels/stop</c> on the old channel —
///      log and continue if it fails.
///
/// Spec: PR-6, "Daily channel renewal".
/// </summary>
public class DriveChannelRenewalJob : IJob
{
    private readonly BackendConfigurationDbContextHelper _dbContextHelper;

    public DriveChannelRenewalJob(BackendConfigurationDbContextHelper dbContextHelper)
    {
        _dbContextHelper = dbContextHelper;
    }

    public async Task Execute()
    {
        await using var db = _dbContextHelper.GetDbContext();
        var settings = DriveServiceSettings.Resolve(db);
        if (!settings.IsConfigured)
        {
            Console.WriteLine("info: DriveChannelRenewalJob - GoogleDrive config missing, skipping");
            return;
        }
        if (string.IsNullOrEmpty(settings.CustomerInstanceUrl))
        {
            Console.WriteLine(
                "info: DriveChannelRenewalJob - CustomerInstanceUrl not configured, skipping (the channel-token JWT needs it)");
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(24);
        var due = await db.DriveWatchChannels
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.ExpiresAt < cutoff)
            .OrderBy(x => x.ExpiresAt)
            .ToListAsync().ConfigureAwait(false);

        Console.WriteLine($"info: DriveChannelRenewalJob - {due.Count} channel(s) to renew");

        var renewed = 0;
        using var oauthProxy = new OAuthProxyClient(settings.MicrotingOAuthProxyUrl, settings.ProxySigningKey);
        using var http = new HttpClient();

        foreach (var oldChannel in due)
        {
            try
            {
                var token = await db.GoogleOAuthTokens
                    .Where(x => x.Id == oldChannel.GoogleOAuthTokenId)
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.RevokedAt == null)
                    .FirstOrDefaultAsync().ConfigureAwait(false);

                if (token == null)
                {
                    Console.WriteLine(
                        $"info: DriveChannelRenewalJob - skipping channel {oldChannel.Id}: token gone or revoked");
                    // Tidy up — the old channel is meaningless without an
                    // active token; soft-delete so we don't keep selecting
                    // it on every tick.
                    await oldChannel.Delete(db).ConfigureAwait(false);
                    continue;
                }

                var refreshToken = RefreshTokenCrypto.Decrypt(
                    token.EncryptedRefreshToken, settings.RefreshTokenEncryptionKey);

                OAuthProxyClient.RefreshResult refreshed;
                try
                {
                    refreshed = await oauthProxy.RefreshAsync(refreshToken).ConfigureAwait(false);
                    token.LastUsedAt = DateTime.UtcNow;
                    await token.Update(db).ConfigureAwait(false);
                }
                catch (OAuthProxyClient.TokenRevokedException ex)
                {
                    Console.WriteLine(
                        $"warn: DriveChannelRenewalJob - token revoked for user {token.UserId}: {ex.Message}");
                    token.RevokedAt = DateTime.UtcNow;
                    await token.Update(db).ConfigureAwait(false);
                    await oldChannel.Delete(db).ConfigureAwait(false);
                    continue;
                }

                var channelId = Guid.NewGuid().ToString("N");
                var now = DateTime.UtcNow;
                var exp = now.AddDays(7);
                var expUnixMillis = ((DateTimeOffset)exp).ToUnixTimeMilliseconds();
                var channelJwt = DriveJwtMinter.MintChannelJwt(
                    settings.CustomerInstanceUrl, channelId, exp, settings.ProxySigningKey);

                var notifyAddress =
                    $"{settings.MicrotingOAuthProxyUrl.TrimEnd('/')}/google-drive/notify";

                using var watchRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://www.googleapis.com/drive/v3/changes/watch?supportsAllDrives=false");
                watchRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
                watchRequest.Content = JsonContent.Create(
                    new DriveWatchRequest
                    {
                        Id = channelId,
                        Type = "web_hook",
                        Address = notifyAddress,
                        Token = channelJwt,
                        Expiration = expUnixMillis.ToString(CultureInfo.InvariantCulture)
                    },
                    options: new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });

                using var watchResponse = await http.SendAsync(watchRequest).ConfigureAwait(false);
                if (!watchResponse.IsSuccessStatusCode)
                {
                    var body = await watchResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Console.WriteLine(
                        $"warn: DriveChannelRenewalJob - watch failed for channel {oldChannel.Id} ({(int)watchResponse.StatusCode}): {body}");
                    continue;
                }

                var responseBody = await watchResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<DriveWatchResponse>(responseBody);
                if (parsed == null)
                {
                    Console.WriteLine(
                        $"warn: DriveChannelRenewalJob - unparseable watch response for channel {oldChannel.Id}");
                    continue;
                }

                DateTime expiresAt;
                if (!string.IsNullOrEmpty(parsed.Expiration)
                    && long.TryParse(parsed.Expiration, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var expirationUnixMs))
                {
                    expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expirationUnixMs).UtcDateTime;
                }
                else
                {
                    expiresAt = exp;
                }

                // Spec: persist the NEW row first, then soft-delete the OLD
                // — flipped relative to EnsureWatchChannelAsync because here
                // we have time to do it cleanly. If anything between the
                // .Create and .Delete throws, the next tick still finds the
                // old row by ExpiresAt and retries; idempotent.
                var newChannel = new DriveWatchChannel
                {
                    GoogleOAuthTokenId = oldChannel.GoogleOAuthTokenId,
                    ChannelId = parsed.Id ?? channelId,
                    ResourceId = parsed.ResourceId ?? string.Empty,
                    SignedToken = channelJwt,
                    ExpiresAt = expiresAt,
                    CreatedByUserId = oldChannel.CreatedByUserId,
                    UpdatedByUserId = oldChannel.UpdatedByUserId
                };
                try
                {
                    await newChannel.Create(db).ConfigureAwait(false);
                }
                catch (Exception persistEx)
                {
                    // Drive accepted the watch but our DB persist failed —
                    // we now have an orphan watch in Drive that we can't
                    // reach (no DB row → no FK to user → no token to mint
                    // a stop request). It will auto-expire within 7 days.
                    // Logged so an operator can monitor for repeats.
                    Console.WriteLine(
                        $"warn: DriveChannelRenewalJob - orphan Drive watch created (channelId={channelId}) for token {oldChannel.GoogleOAuthTokenId}: {persistEx.Message}");
                    SentrySdk.CaptureException(persistEx);
                    continue;
                }

                // Best-effort stop on the old channel. Drive accepts the
                // channel-stop call as long as id+resourceId+access-token
                // line up; failures here are not fatal because the channel
                // would expire on its own within 24h.
                if (!string.IsNullOrEmpty(oldChannel.ChannelId)
                    && !string.IsNullOrEmpty(oldChannel.ResourceId))
                {
                    try
                    {
                        using var stopRequest = new HttpRequestMessage(
                            HttpMethod.Post,
                            "https://www.googleapis.com/drive/v3/channels/stop");
                        stopRequest.Headers.Authorization =
                            new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
                        stopRequest.Content = JsonContent.Create(new DriveChannelStopRequest
                        {
                            Id = oldChannel.ChannelId,
                            ResourceId = oldChannel.ResourceId
                        });

                        using var stopResponse = await http.SendAsync(stopRequest).ConfigureAwait(false);
                        if (!stopResponse.IsSuccessStatusCode && stopResponse.StatusCode != System.Net.HttpStatusCode.NoContent)
                        {
                            var body = await stopResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            Console.WriteLine(
                                $"warn: DriveChannelRenewalJob - channels.stop returned {(int)stopResponse.StatusCode}: {body}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"warn: DriveChannelRenewalJob - channels.stop failed for {oldChannel.ChannelId}: {ex.Message}");
                    }
                }

                await oldChannel.Delete(db).ConfigureAwait(false);
                renewed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"fail: DriveChannelRenewalJob - {ex.Message}");
                Console.WriteLine($"fail: {ex.StackTrace}");
                SentrySdk.CaptureException(ex);
                // Continue with the rest of the queue — one bad row
                // shouldn't sink the whole job.
            }
        }

        Console.WriteLine($"info: DriveChannelRenewalJob - renewed {renewed} of {due.Count} channel(s)");
    }

#nullable enable
    private sealed class DriveWatchRequest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("address")] public string Address { get; set; } = "";
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expiration")] public string? Expiration { get; set; }
    }

    private sealed class DriveWatchResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("resourceId")] public string? ResourceId { get; set; }
        [JsonPropertyName("expiration")] public string? Expiration { get; set; }
    }
#nullable disable

    private sealed class DriveChannelStopRequest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("resourceId")] public string ResourceId { get; set; } = "";
    }
}
