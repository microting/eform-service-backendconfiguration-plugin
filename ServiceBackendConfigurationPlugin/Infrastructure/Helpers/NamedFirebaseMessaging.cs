using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// The FirebaseMessaging client of a NAMED FirebaseApp, created on first use.
/// Every FCM sender in this service owns one named app per Firebase project
/// (adhoc: "microting-adhoc", chemistry: "microting-chemistry"). The unnamed
/// default app is a process-wide singleton that co-hosted plugins also claim;
/// the full reasoning lives on AdhocReminderJob.EnsureAdhocMessaging.
/// </summary>
public static class NamedFirebaseMessaging
{
    // Serialises first-use FirebaseApp.Create across all named apps.
    private static readonly object InitLock = new();

    public static FirebaseMessaging Get(string appName, string serviceAccountJson)
    {
        var app = FirebaseApp.GetInstance(appName);
        if (app == null)
        {
            lock (InitLock)
            {
                // Re-read inside the lock: FirebaseApp.Create THROWS when an app
                // of this name already exists, so a racing first use must see
                // the winner's app instead of attempting a second Create.
                app = FirebaseApp.GetInstance(appName);
                if (app == null)
                {
                    var options = new AppOptions
                    {
                        // Non-obsolete replacement for GoogleCredential.FromJson;
                        // pinning ServiceAccountCredential fails fast on a JSON
                        // that is not a service-account key.
                        Credential = CredentialFactory
                            .FromJson<ServiceAccountCredential>(serviceAccountJson)
                            .ToGoogleCredential()
                    };

                    app = FirebaseApp.Create(options, appName);
                }
            }
        }

        return FirebaseMessaging.GetMessaging(app);
    }
}
