namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System.Threading.Tasks;
using FirebaseAdmin;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>
/// The chemistry sender pushes through its own Firebase project
/// (microting-chemistry). It must never claim the process-wide default app
/// and must coexist with adhoc's named app in the same MicrotingService
/// process — see AdhocReminderJob.EnsureAdhocMessaging for the failure mode.
/// Offline: creating apps and messaging clients sends nothing.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ChemistryFirebaseAppIsolationTests
{
    [SetUp]
    public void ResetBefore() => ResetFirebaseApps();

    [TearDown]
    public void ResetAfter() => ResetFirebaseApps();

    private static void ResetFirebaseApps()
    {
        FirebaseApp.GetInstance(FirebaseChemicalPushSender.FirebaseAppName)?.Delete();
        FirebaseApp.GetInstance(AdhocReminderJob.FirebaseAppName)?.Delete();
        FirebaseApp.GetInstance(TailBiteDailyJob.FirebaseAppName)?.Delete();
        FirebaseApp.DefaultInstance?.Delete();
    }

    [Test]
    public void ChemistryApp_IsNamed_AndTheDefaultAppIsLeftUntouched()
    {
        Assert.That(FirebaseApp.DefaultInstance, Is.Null, "precondition: no default app");

        var messaging = NamedFirebaseMessaging.Get(FirebaseChemicalPushSender.FirebaseAppName,
            TestServiceAccounts.Json("microting-chemistry-test"));

        Assert.That(messaging, Is.Not.Null);
        Assert.That(FirebaseApp.GetInstance(FirebaseChemicalPushSender.FirebaseAppName)?.Name,
            Is.EqualTo("microting-chemistry"));
        Assert.That(FirebaseApp.DefaultInstance, Is.Null);
    }

    [Test]
    public void AdhocAndChemistry_AreSeparateAppsAndClients()
    {
        var adhoc = AdhocReminderJob.EnsureAdhocMessaging(TestServiceAccounts.Json("microting-adhoc-test"));
        var chemistry = NamedFirebaseMessaging.Get(FirebaseChemicalPushSender.FirebaseAppName,
            TestServiceAccounts.Json("microting-chemistry-test"));

        Assert.That(chemistry, Is.Not.SameAs(adhoc));
        Assert.That(FirebaseApp.GetInstance(AdhocReminderJob.FirebaseAppName),
            Is.Not.SameAs(FirebaseApp.GetInstance(FirebaseChemicalPushSender.FirebaseAppName)));
    }

    [Test]
    public void Halebid_IsItsOwnApp_IsolatedFromChemistryAndAdhoc()
    {
        var halebid = TailBiteDailyJob.EnsureHalebidMessaging(TestServiceAccounts.Json("microting-halebid-test"));
        var adhoc = AdhocReminderJob.EnsureAdhocMessaging(TestServiceAccounts.Json("microting-adhoc-test"));
        var chemistry = NamedFirebaseMessaging.Get(FirebaseChemicalPushSender.FirebaseAppName,
            TestServiceAccounts.Json("microting-chemistry-test"));

        Assert.That(halebid, Is.Not.SameAs(adhoc));
        Assert.That(halebid, Is.Not.SameAs(chemistry));
        Assert.That(FirebaseApp.GetInstance(TailBiteDailyJob.FirebaseAppName)?.Name, Is.EqualTo("microting-halebid"));
        Assert.That(FirebaseApp.GetInstance(TailBiteDailyJob.FirebaseAppName),
            Is.Not.SameAs(FirebaseApp.GetInstance(AdhocReminderJob.FirebaseAppName)));
        Assert.That(FirebaseApp.GetInstance(TailBiteDailyJob.FirebaseAppName),
            Is.Not.SameAs(FirebaseApp.GetInstance(FirebaseChemicalPushSender.FirebaseAppName)));
        Assert.That(FirebaseApp.DefaultInstance, Is.Null);
    }

    [Test]
    public void RepeatedGets_ReuseTheSameClient()
    {
        var json = TestServiceAccounts.Json("microting-chemistry-test");

        var first = NamedFirebaseMessaging.Get(FirebaseChemicalPushSender.FirebaseAppName, json);
        var second = NamedFirebaseMessaging.Get(FirebaseChemicalPushSender.FirebaseAppName, json);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public async Task Sender_WithNoMessages_ReturnsNothing_AndCreatesNoApp()
    {
        var outcomes = await new FirebaseChemicalPushSender().SendAsync("not even json", []);

        Assert.That(outcomes, Is.Empty);
        Assert.That(FirebaseApp.GetInstance(FirebaseChemicalPushSender.FirebaseAppName), Is.Null);
    }
}
