#pragma warning disable CS0618 // Message.Token, see ChemicalPushMessages.ToFcmMessage
namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

[TestFixture]
public class ChemicalAlertJobTests : ChemicalJobTestBase
{
    // Thursday 2026-10-01 05:10 UTC: inside the run hour; Copenhagen date 2026-10-01.
    private static readonly DateTimeOffset RunTime = new(2026, 10, 1, 5, 10, 0, TimeSpan.Zero);
    private static readonly DateTime SixMonthsAway = new(2027, 3, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AlreadyPassed = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FarAway = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private FakeChemicalPushSender _sender;
    private FakeChemicalSdkDirectory _sdk;
    private FixedTimeProvider _time;

    [SetUp]
    public void CreateDoubles()
    {
        _sender = new FakeChemicalPushSender();
        _sdk = new FakeChemicalSdkDirectory();
        _time = new FixedTimeProvider(RunTime);
    }

    private ChemicalAlertJob CreateJob() => new(DbHelper, ChemicalHelper, _sdk, _sender, _time);

    private async Task EnablePushAsync(bool flag = true)
    {
        if (flag)
        {
            await SetPluginValueAsync(ChemicalAlertJob.FeatureFlagKey, "true");
        }

        await SetPluginValueAsync(ChemicalAlertJob.ServiceAccountJsonKey, "{\"type\":\"service_account\"}");
    }

    /// <summary>One property, one open placement, one Admin worker (site 501) with one chemistry device.</summary>
    private async Task<(Property Property, ChemicalPlacement Placement)> ArrangeAsync(
        DateTime? possession = null, int status = 5, int? observed = 5)
    {
        var property = await CreatePropertyAsync("Gården");
        var location = await CreateLocationAsync(property.Id);
        var chemicalId = await AddChemicalAsync("Roundup Flex", status, possession: possession ?? SixMonthsAway);
        var placement = await CreatePlacementAsync(location.Id, chemicalId, observed);
        await AddWorkerAsync(property.Id, 501, admin: true);
        await AddTokenAsync(501, "admin-phone");
        return (property, placement);
    }

    private static async Task<List<ChemicalAlertLog>> LogsAsync()
    {
        await using var db = FreshDb();
        return await db.ChemicalAlertLogs.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }

    private static async Task<ChemicalPlacement> ReloadAsync(int placementId)
    {
        await using var db = FreshDb();
        return await db.ChemicalPlacements.AsNoTracking().SingleAsync(x => x.Id == placementId);
    }

    private List<string> SentTokens() => _sender.Sent.Select(s => s.Message.Token).ToList();

    [TestCase(null)]
    [TestCase("false")]
    [TestCase("yes")]
    public async Task FlagNotTrue_SendsNothingAndLogsNothing(string flagValue)
    {
        await ArrangeAsync();
        if (flagValue != null)
        {
            await SetPluginValueAsync(ChemicalAlertJob.FeatureFlagKey, flagValue);
        }

        await SetPluginValueAsync(ChemicalAlertJob.ServiceAccountJsonKey, "{}");

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [TestCase(4, 59)]
    [TestCase(6, 0)]
    public async Task OutsideTheRunHour_DoesNothing(int hour, int minute)
    {
        await ArrangeAsync();
        await EnablePushAsync();
        _time.Now = new DateTimeOffset(2026, 10, 1, hour, minute, 0, TimeSpan.Zero);

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [Test]
    public async Task CredentialMissing_SendsNothingAndLogsNothing()
    {
        await ArrangeAsync();
        await SetPluginValueAsync(ChemicalAlertJob.FeatureFlagKey, "true");

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [Test]
    public async Task SixMonths_PushesAdminAndRemoveWorkersOnly_AndLogsOnce()
    {
        var (property, placement) = await ArrangeAsync();
        await AddWorkerAsync(property.Id, 502, remove: true);
        await AddTokenAsync(502, "remover-phone");
        await AddWorkerAsync(property.Id, 503, view: true, stock: true);
        await AddTokenAsync(503, "viewer-phone");
        await AddWorkerAsync(property.Id, 504, admin: true, removedFromProperty: true);
        await AddTokenAsync(504, "former-worker-phone");
        var otherProperty = await CreatePropertyAsync("Anden gård");
        await AddWorkerAsync(otherProperty.Id, 505, admin: true);
        await AddTokenAsync(505, "other-property-phone");
        await AddTokenAsync(501, "adhoc-phone", appId: "adhoc");
        await AddTokenAsync(501, "eform-phone", appId: "eform");
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(SentTokens(), Is.EquivalentTo(new[] { "admin-phone", "remover-phone" }));
        Assert.That(_sender.Sent.Select(s => s.Message.Data["type"]).Distinct(), Is.EqualTo(new[] { "chemical_alert" }));
        Assert.That(_sender.Sent.Select(s => s.Message.Data["customerNo"]), Has.All.EqualTo("4711"),
            "the tenant's customerNo travels in every payload");
        var log = (await LogsAsync()).Single();
        Assert.That(log.PlacementId, Is.EqualTo(placement.Id));
        Assert.That(log.Threshold, Is.EqualTo(ChemicalAlertThresholdEnum.SixMonths));
        Assert.That(log.DeadlineKind, Is.EqualTo(ChemicalDeadlineKindEnum.UseAndPossession));
        Assert.That(log.Status, Is.Null);
        Assert.That(log.Channel, Is.EqualTo(ChemicalAlertChannelEnum.Push));
        Assert.That(log.SentAt, Is.EqualTo(RunTime.UtcDateTime));
    }

    [Test]
    public async Task SecondRunSameDay_SendsNothingNew()
    {
        await ArrangeAsync();
        await EnablePushAsync();

        await CreateJob().RunAsync();
        _time.Now = RunTime.AddMinutes(40); // a restart inside the same hour
        await CreateJob().RunAsync();

        Assert.That(SentTokens(), Is.EqualTo(new[] { "admin-phone" }));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task TransientFailure_LogsNothing_AndTheNextRunRetries()
    {
        await ArrangeAsync();
        await EnablePushAsync();
        _sender.Outcome = _ => ChemicalPushOutcome.Transient;

        await CreateJob().RunAsync();
        Assert.That(await LogsAsync(), Is.Empty);

        _sender.Outcome = _ => ChemicalPushOutcome.Delivered;
        _time.Now = RunTime.AddDays(1);
        await CreateJob().RunAsync();

        Assert.That(SentTokens(), Is.EqualTo(new[] { "admin-phone", "admin-phone" }));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AllSenderIdMismatch_PrunesNothingAndLogsNothing()
    {
        await ArrangeAsync();
        await AddTokenAsync(501, "admin-tablet");
        await EnablePushAsync();
        _sender.Outcome = _ => ChemicalPushOutcome.SenderIdMismatch;

        await CreateJob().RunAsync();

        await using var db = FreshDb();
        var live = await db.DeviceTokens.AsNoTracking()
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created).Select(x => x.FcmToken).ToListAsync();
        Assert.That(live, Is.EquivalentTo(new[] { "admin-phone", "admin-tablet" }), "a credential fault must not prune tokens");
        Assert.That(await LogsAsync(), Is.Empty, "a credential fault retries on the next run");
    }

    [Test]
    public async Task AllInvalidArgument_IsSystemic_PrunesNothingAndLogsNothing()
    {
        await ArrangeAsync();
        await AddTokenAsync(501, "admin-tablet");
        await EnablePushAsync();
        _sender.Outcome = _ => ChemicalPushOutcome.InvalidArgument;

        await CreateJob().RunAsync();

        await using var db = FreshDb();
        var live = await db.DeviceTokens.AsNoTracking()
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created).Select(x => x.FcmToken).ToListAsync();
        Assert.That(live, Is.EquivalentTo(new[] { "admin-phone", "admin-tablet" }), "a payload fault must not prune tokens");
        Assert.That(await LogsAsync(), Is.Empty, "a payload fault retries on the next run");
    }

    [Test]
    public async Task MixedInvalidArgumentAndDead_PrunesOnlyThoseTokens_AndLogs()
    {
        await ArrangeAsync();
        await AddTokenAsync(501, "bad-phone");
        await AddTokenAsync(501, "dead-phone");
        await EnablePushAsync();
        _sender.Outcome = m => m.Token switch
        {
            "bad-phone" => ChemicalPushOutcome.InvalidArgument,
            "dead-phone" => ChemicalPushOutcome.DeadToken,
            _ => ChemicalPushOutcome.Delivered,
        };

        await CreateJob().RunAsync();

        await using var db = FreshDb();
        var live = await db.DeviceTokens.AsNoTracking()
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created).Select(x => x.FcmToken).ToListAsync();
        Assert.That(live, Is.EqualTo(new[] { "admin-phone" }));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CustomerNoEmpty_SendsNothingAndLogsNothing()
    {
        await ArrangeAsync();
        await EnablePushAsync();
        _sdk.CustomerNo = "";

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [Test]
    public async Task CustomerNoSettingMissing_DoesNotThrow_SendsNothingAndLogsNothing()
    {
        await ArrangeAsync();
        await EnablePushAsync();
        _sdk.CustomerNoThrows = true;

        Assert.That(async () => await CreateJob().RunAsync(), Throws.Nothing);

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [Test]
    public async Task CustomerNo_IsReadOnlyWhenAlertsAreDue()
    {
        await ArrangeAsync(possession: FarAway);
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(_sdk.CustomerNoReads, Is.Zero);
    }

    [TestCase(ChemicalPushOutcome.InvalidArgument, true)]
    [TestCase(ChemicalPushOutcome.SenderIdMismatch, true)]
    [TestCase(ChemicalPushOutcome.DeadToken, false)]
    [TestCase(ChemicalPushOutcome.Delivered, false)]
    [TestCase(ChemicalPushOutcome.Transient, false)]
    public void IsSystemicBatch_OnlyWhenEveryOutcomeIsTheSameFault(ChemicalPushOutcome outcome, bool systemic)
    {
        Assert.That(ChemicalAlertJob.IsSystemicBatch([outcome, outcome]), Is.EqualTo(systemic));
        Assert.That(ChemicalAlertJob.IsSystemicBatch([outcome, ChemicalPushOutcome.Delivered]), Is.False);
        Assert.That(ChemicalAlertJob.IsSystemicBatch([]), Is.False);
    }

    private async Task<List<string>> LiveTokensAsync()
    {
        await using var db = FreshDb();
        return await db.DeviceTokens.AsNoTracking()
            .Where(x => x.WorkflowState == Constants.WorkflowStates.Created).Select(x => x.FcmToken).ToListAsync();
    }

    /// <summary>A second property with its own due alert, one Admin worker (site 601) and one device.</summary>
    private async Task<ChemicalPlacement> ArrangeSecondPropertyAsync()
    {
        var property = await CreatePropertyAsync("Anden gård");
        var location = await CreateLocationAsync(property.Id);
        var chemicalId = await AddChemicalAsync("Basta", possession: SixMonthsAway);
        var placement = await CreatePlacementAsync(location.Id, chemicalId);
        await AddWorkerAsync(property.Id, 601, admin: true);
        await AddTokenAsync(601, "second-phone");
        return placement;
    }

    [Test]
    public async Task PartialTransient_LogsOnce_NoResendNextDay()
    {
        await ArrangeAsync();
        await AddTokenAsync(501, "flaky-tablet");
        await EnablePushAsync();
        _sender.Outcome = m => m.Token == "flaky-tablet" ? ChemicalPushOutcome.Transient : ChemicalPushOutcome.Delivered;

        await CreateJob().RunAsync();
        _time.Now = RunTime.AddDays(1);
        await CreateJob().RunAsync();

        Assert.That(SentTokens().Count(t => t == "admin-phone"), Is.EqualTo(1), "the delivered device is not re-pushed");
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CredentialFaultWithOneTransient_PrunesNothing_AndLogsNothing()
    {
        await ArrangeAsync();
        await AddTokenAsync(501, "admin-tablet");
        await AddTokenAsync(501, "flaky-watch");
        await EnablePushAsync();
        _sender.Outcome = m => m.Token == "flaky-watch" ? ChemicalPushOutcome.Transient : ChemicalPushOutcome.SenderIdMismatch;

        await CreateJob().RunAsync();

        Assert.That(await LiveTokensAsync(), Is.EquivalentTo(new[] { "admin-phone", "admin-tablet", "flaky-watch" }));
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [Test]
    public async Task InvalidArgumentOnOneProperty_PrunedWhenAnotherPropertyDelivered()
    {
        await ArrangeAsync();
        await ArrangeSecondPropertyAsync();
        await EnablePushAsync();
        _sender.Outcome = m => m.Token == "admin-phone" ? ChemicalPushOutcome.InvalidArgument : ChemicalPushOutcome.Delivered;

        await CreateJob().RunAsync();

        Assert.That(await LiveTokensAsync(), Is.EqualTo(new[] { "second-phone" }), "the run proved the payload good");
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1), "only the delivered property is logged");
    }

    [Test]
    public async Task MixedSenderIdMismatchAndDelivered_PrunesTheMismatchedToken()
    {
        await ArrangeAsync();
        await AddTokenAsync(501, "foreign-phone");
        await EnablePushAsync();
        _sender.Outcome = m => m.Token == "foreign-phone" ? ChemicalPushOutcome.SenderIdMismatch : ChemicalPushOutcome.Delivered;

        await CreateJob().RunAsync();

        Assert.That(await LiveTokensAsync(), Is.EqualTo(new[] { "admin-phone" }));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task OneFailingProperty_DoesNotStopTheOthers()
    {
        await ArrangeAsync();
        var second = await ArrangeSecondPropertyAsync();
        await EnablePushAsync();
        _sender.Outcome = m => m.Token == "admin-phone"
            ? throw new InvalidOperationException("boom")
            : ChemicalPushOutcome.Delivered;

        Assert.That(async () => await CreateJob().RunAsync(), Throws.Nothing);

        var log = (await LogsAsync()).Single();
        Assert.That(log.PlacementId, Is.EqualTo(second.Id));
    }

    [Test]
    public async Task CustomerNo_IsReadExactlyOncePerRun_EvenWithTwoPropertiesDue()
    {
        await ArrangeAsync();
        await ArrangeSecondPropertyAsync();
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(_sdk.CustomerNoReads, Is.EqualTo(1));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DeadToken_IsSoftDeleted_AndTheAlertIsLogged()
    {
        await ArrangeAsync();
        await AddTokenAsync(501, "dead-phone");
        await EnablePushAsync();
        _sender.Outcome = m => m.Token == "dead-phone" ? ChemicalPushOutcome.DeadToken : ChemicalPushOutcome.Delivered;

        await CreateJob().RunAsync();

        await using var db = FreshDb();
        var dead = await db.DeviceTokens.AsNoTracking().SingleAsync(x => x.FcmToken == "dead-phone");
        Assert.That(dead.WorkflowState, Is.EqualTo(Constants.WorkflowStates.Removed));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task NoDevices_AlertIsStillLogged_SoNoLateBurst()
    {
        var property = await CreatePropertyAsync();
        var location = await CreateLocationAsync(property.Id);
        var chemicalId = await AddChemicalAsync("Roundup Flex", possession: SixMonthsAway);
        await CreatePlacementAsync(location.Id, chemicalId);
        await AddWorkerAsync(property.Id, 501, admin: true);
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task StatusChangedToDeregistered_PushesAndStoresObservedStatus()
    {
        var (_, placement) = await ArrangeAsync(possession: FarAway, status: 6, observed: 5);
        await EnablePushAsync();

        await CreateJob().RunAsync();

        var message = _sender.Sent.Single().Message;
        Assert.That(message.Data["threshold"], Is.EqualTo("status_changed"));
        Assert.That(message.Data["status"], Is.EqualTo("6"));
        var log = (await LogsAsync()).Single();
        Assert.That(log.Threshold, Is.EqualTo(ChemicalAlertThresholdEnum.StatusChanged));
        Assert.That(log.Status, Is.EqualTo(6));
        Assert.That((await ReloadAsync(placement.Id)).ObservedStatus, Is.EqualTo(6));
    }

    [Test]
    public async Task StatusChange_TransientFailure_KeepsObservedStatusForRetry()
    {
        var (_, placement) = await ArrangeAsync(possession: FarAway, status: 7, observed: 5);
        await EnablePushAsync();
        _sender.Outcome = _ => ChemicalPushOutcome.Transient;

        await CreateJob().RunAsync();

        Assert.That((await ReloadAsync(placement.Id)).ObservedStatus, Is.EqualTo(5));
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [Test]
    public async Task StatusBackToApproved_UpdatesSilently()
    {
        var (_, placement) = await ArrangeAsync(possession: FarAway, status: 5, observed: 6);
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That((await ReloadAsync(placement.Id)).ObservedStatus, Is.EqualTo(5));
    }

    [Test]
    public async Task MovedPlacement_DoesNotRepeatAncestorAlert()
    {
        var (property, source) = await ArrangeAsync(possession: AlreadyPassed);
        await EnablePushAsync();
        await CreateJob().RunAsync();
        Assert.That(_sender.Sent, Has.Count.EqualTo(1), "arrange: the Passed alert went out once");

        await ClosePlacementAsync(source, ChemicalRemovalReasonEnum.Moved);
        var target = await CreateLocationAsync(property.Id, "Lade");
        await CreatePlacementAsync(target.Id, source.ChemicalId, movedFromPlacementId: source.Id);
        _time.Now = RunTime.AddDays(1);
        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Has.Count.EqualTo(1));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FourAlertsOnOneProperty_OneSummaryPerDevice()
    {
        var property = await CreatePropertyAsync("Gården");
        var location = await CreateLocationAsync(property.Id);
        for (var i = 0; i < 4; i++)
        {
            var chemicalId = await AddChemicalAsync($"Produkt {i}", possession: AlreadyPassed);
            await CreatePlacementAsync(location.Id, chemicalId);
        }

        await AddWorkerAsync(property.Id, 501, admin: true);
        await AddTokenAsync(501, "admin-phone");
        await AddTokenAsync(501, "admin-tablet");
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(SentTokens(), Is.EquivalentTo(new[] { "admin-phone", "admin-tablet" }));
        Assert.That(_sender.Sent.Select(s => s.Message.Data["type"]), Has.All.EqualTo("chemical_alert_summary"));
        Assert.That(_sender.Sent.Select(s => s.Message.Data["count"]), Has.All.EqualTo("4"));
        Assert.That(await LogsAsync(), Has.Count.EqualTo(4), "every alert is logged individually");
    }

    [Test]
    public async Task EachWorker_GetsTheirLanguage()
    {
        var (property, _) = await ArrangeAsync();
        await AddWorkerAsync(property.Id, 502, remove: true);
        await AddTokenAsync(502, "remover-phone");
        _sdk.Languages[501] = "da";
        _sdk.Languages[502] = "de-DE";
        await EnablePushAsync();

        await CreateJob().RunAsync();

        var titles = _sender.Sent.ToDictionary(s => s.Message.Token, s => s.Message.Notification.Title);
        Assert.That(titles["admin-phone"], Is.EqualTo("Brugs- og besiddelsesfrist om under 6 måneder"));
        Assert.That(titles["remover-phone"], Is.EqualTo("Use and possession deadline in less than 6 months"));
    }

    [Test]
    public async Task RemovedPlacement_IsIgnored()
    {
        var (_, placement) = await ArrangeAsync(possession: AlreadyPassed);
        await ClosePlacementAsync(placement, ChemicalRemovalReasonEnum.Disposed);
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent, Is.Empty);
        Assert.That(await LogsAsync(), Is.Empty);
    }

    [Test]
    public async Task SendsWithTheTenantCredential()
    {
        await ArrangeAsync();
        await EnablePushAsync();

        await CreateJob().RunAsync();

        Assert.That(_sender.Sent.Single().ServiceAccountJson, Is.EqualTo("{\"type\":\"service_account\"}"));
    }
}
