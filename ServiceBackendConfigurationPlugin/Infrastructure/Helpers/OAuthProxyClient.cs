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
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Service-side mirror of <c>BackendConfiguration.Pn.Services.GoogleDrive.OAuthProxyClient</c>.
/// HMAC scheme matches the proxy contract documented in PR-1:
///   canonical string = "{body}\n{date}" (lowercase hex SHA-256, key =
///   <c>GoogleDrive:ProxySigningKey</c>, sent in
///   <c>Authorization: HMAC-SHA256 &lt;hex&gt;</c>).
///
/// Code is duplicated from the plugin for PR-6 scope; shared abstraction
/// can be extracted into the base package once usage stabilises.
/// </summary>
internal sealed class OAuthProxyClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _proxySigningKey;

    public OAuthProxyClient(string proxyBaseUrl, string proxySigningKey)
    {
        if (string.IsNullOrEmpty(proxyBaseUrl))
        {
            throw new InvalidOperationException(
                "GoogleDrive:MicrotingOAuthProxyUrl is not configured.");
        }
        if (string.IsNullOrEmpty(proxySigningKey))
        {
            throw new InvalidOperationException(
                "GoogleDrive:ProxySigningKey is not configured.");
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(proxyBaseUrl.EndsWith('/') ? proxyBaseUrl : proxyBaseUrl + "/")
        };
        _proxySigningKey = proxySigningKey;
    }

    public sealed class RefreshResult
    {
        public string AccessToken { get; set; } = "";
        public DateTime AccessTokenExpiry { get; set; }
    }

    /// <summary>
    /// Thrown when Google rejects the refresh token (401 / invalid_grant).
    /// Caller should mark the row's <c>RevokedAt</c>.
    /// </summary>
    public sealed class TokenRevokedException(string message) : Exception(message);

    public async Task<RefreshResult> RefreshAsync(string refreshToken)
    {
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(
            new RefreshRequestPayload { RefreshToken = refreshToken });

        var dateHeader = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        var canonical = new byte[bodyBytes.Length + 1 + Encoding.UTF8.GetByteCount(dateHeader)];
        Buffer.BlockCopy(bodyBytes, 0, canonical, 0, bodyBytes.Length);
        canonical[bodyBytes.Length] = (byte)'\n';
        var dateBytes = Encoding.UTF8.GetBytes(dateHeader);
        Buffer.BlockCopy(dateBytes, 0, canonical, bodyBytes.Length + 1, dateBytes.Length);

        var keyBytes = Encoding.UTF8.GetBytes(_proxySigningKey);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(canonical);
        var hex = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, "google-drive/refresh")
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Date", dateHeader);
        request.Headers.Authorization = new AuthenticationHeaderValue("HMAC-SHA256", hex);

        using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (errBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                throw new TokenRevokedException(
                    "Google refresh token rejected by Google (invalid_grant).");
            }

            throw new TokenRevokedException(
                $"OAuth proxy returned 401 with body: {errBody}");
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync<RefreshResponsePayload>()
            .ConfigureAwait(false);

        if (payload == null || string.IsNullOrEmpty(payload.AccessToken))
        {
            throw new InvalidOperationException(
                "OAuth proxy returned a successful response with no access token.");
        }

        return new RefreshResult
        {
            AccessToken = payload.AccessToken,
            AccessTokenExpiry = payload.AccessTokenExpiry
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class RefreshRequestPayload
    {
        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = "";
    }

    private sealed class RefreshResponsePayload
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("accessTokenExpiry")]
        public DateTime AccessTokenExpiry { get; set; }
    }
}
