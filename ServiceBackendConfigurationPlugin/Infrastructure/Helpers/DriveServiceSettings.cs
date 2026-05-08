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
using Microting.EformBackendConfigurationBase.Infrastructure.Data;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// Bundles the Google Drive deployment-time config the cron jobs need.
///
/// The service host has no <c>IConfiguration</c> bound to a
/// <c>GoogleDrive</c> section the way the plugin process does, so we resolve
/// values cross-process via two sources, in order:
///   1) Environment variable using ASP.NET-style <c>__</c> separator
///      (e.g. <c>GoogleDrive__MicrotingOAuthProxyUrl</c>).
///   2) <c>PluginConfigurationValues</c> row using the <c>GoogleDrive:Xxx</c>
///      key the plugin writes (when seeded). Same DB, no extra hop.
/// Empty values surface to the caller as empty strings; the cron jobs
/// short-circuit ("not configured, skipping") rather than throwing on every
/// tick — the keep-alive loop and renewal loop are no-ops on an
/// un-configured deployment.
/// </summary>
internal sealed class DriveServiceSettings
{
    public string MicrotingOAuthProxyUrl { get; init; } = "";
    public string ProxySigningKey { get; init; } = "";
    public string RefreshTokenEncryptionKey { get; init; } = "";
    public string CustomerInstanceUrl { get; init; } = "";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(MicrotingOAuthProxyUrl)
        && !string.IsNullOrEmpty(ProxySigningKey)
        && !string.IsNullOrEmpty(RefreshTokenEncryptionKey);

    public static DriveServiceSettings Resolve(BackendConfigurationPnDbContext dbContext)
    {
        return new DriveServiceSettings
        {
            MicrotingOAuthProxyUrl = ReadKey(dbContext, "GoogleDrive:MicrotingOAuthProxyUrl"),
            ProxySigningKey = ReadKey(dbContext, "GoogleDrive:ProxySigningKey"),
            RefreshTokenEncryptionKey = ReadKey(dbContext, "GoogleDrive:RefreshTokenEncryptionKey"),
            CustomerInstanceUrl = ReadKey(
                dbContext,
                "GoogleDrive:CustomerInstanceUrl",
                fallbackKey: "BackendConfigurationSettings:CustomerInstanceUrl")
        };
    }

    private static string ReadKey(BackendConfigurationPnDbContext dbContext, string key, string fallbackKey = null)
    {
        // Env var first — operationally easiest knob to flip without a
        // database change. Both colon and ASP.NET double-underscore shapes
        // are accepted because docker/k8s envs typically forbid colons.
        var envKey = key.Replace(':', '_').Replace("_", "__");
        var fromEnv = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

        fromEnv = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

        var fromDb = dbContext.PluginConfigurationValues
            .Where(x => x.Name == key)
            .Select(x => x.Value)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(fromDb)) return fromDb;

        if (!string.IsNullOrEmpty(fallbackKey))
        {
            var fbEnvKey = fallbackKey.Replace(':', '_').Replace("_", "__");
            var fbFromEnv = Environment.GetEnvironmentVariable(fbEnvKey)
                            ?? Environment.GetEnvironmentVariable(fallbackKey);
            if (!string.IsNullOrEmpty(fbFromEnv)) return fbFromEnv;

            var fbFromDb = dbContext.PluginConfigurationValues
                .Where(x => x.Name == fallbackKey)
                .Select(x => x.Value)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(fbFromDb)) return fbFromDb;
        }

        return string.Empty;
    }
}
