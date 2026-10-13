namespace ServiceBackendConfigurationPlugin.Integration.Test;

using System;
using System.Security.Cryptography;

public static class TestServiceAccounts
{
    /// <summary>
    /// A syntactically real service-account key. The private key is generated
    /// here rather than checked in, so nothing in this repo resembles a leaked
    /// credential; FirebaseAdmin parses it eagerly, so a placeholder would not do.
    /// </summary>
    public static string Json(string projectId)
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");

        return $$"""
                 {
                   "type": "service_account",
                   "project_id": "{{projectId}}",
                   "private_key_id": "{{Guid.NewGuid():N}}",
                   "private_key": "{{privateKey}}",
                   "client_email": "test@{{projectId}}.iam.gserviceaccount.com",
                   "client_id": "1",
                   "token_uri": "https://oauth2.googleapis.com/token"
                 }
                 """;
    }
}
