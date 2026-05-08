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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Mirrors <c>BackendConfiguration.Pn.Services.GoogleDrive.GoogleDriveAuthService.MintChannelJwt</c>.
/// Mints a <c>{typ:"channel", customerInstanceUrl, channelId, exp}</c>
/// HS256-signed JWT. Used by PR-6's daily channel renewal to mint fresh
/// channel-token JWTs that the OAuth proxy validates before fanning Drive
/// notifications back to the customer instance.
/// </summary>
internal static class DriveJwtMinter
{
    public static string MintChannelJwt(string customerInstanceUrl, string channelId, DateTime exp, string proxySigningKey)
    {
        if (string.IsNullOrEmpty(proxySigningKey))
        {
            throw new InvalidOperationException(
                "GoogleDrive:ProxySigningKey is not configured.");
        }

        var header = new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };
        var payload = new Dictionary<string, object>
        {
            ["typ"] = "channel",
            ["customerInstanceUrl"] = customerInstanceUrl,
            ["channelId"] = channelId,
            ["exp"] = ((DateTimeOffset)exp).ToUnixTimeSeconds()
        };

        var headerB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(proxySigningKey));
        var signature = Base64UrlEncode(hmac.ComputeHash(signingInput));
        return $"{headerB64}.{payloadB64}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
