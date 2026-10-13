namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

[TestFixture]
public class ChemicalDigestJobTests : ChemicalJobTestBase
{
    // Monday 2026-10-26 06:10 UTC = 07:10 CET (the first Monday after DST ended).
    private static readonly DateTimeOffset RunTime = new(2026, 10, 26, 6, 10, 0, TimeSpan.Zero);
    private static readonly DateTime AlreadyPassed = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FarAway = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private FakeChemicalEmailSender _email;
    private FakeChemicalSdkDirectory _sdk;
    private FixedTimeProvider _time;

    [SetUp]
    public void CreateDoubles()
    {
        _email = new FakeChemicalEmailSender();
        _sdk = new FakeChemicalSdkDirectory { CustomerNo = "4711" };
        _time = new FixedTimeProvider(RunTime);
    }

    private ChemicalDigestJob CreateJob() => new(DbHelper, ChemicalHelper, _sdk, _email, _time);

    private static DateTimeOffset At(string utcIso) =>
        DateTimeOffset.Parse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>Property "Gården" with one open placement whose possession deadline has passed.</summary>
    private async Task<(Property Property, ChemicalPlacement Placement)> ArrangeAsync(
        string recipients = "a@example.com,b@example.com", string propertyName = "Gården", DateTime? possession = null,
        DateTime? registeredAt = null)
    {
        var property = await CreatePropertyAsync(propertyName);
        var location = await CreateLocationAsync(property.Id);
        var chemicalId = await AddChemicalAsync("Roundup Flex", possession: possession ?? AlreadyPassed);
        var placement = await CreatePlacementAsync(location.Id, chemicalId, registeredAt: registeredAt ?? LongAgo);
        await SetDigestRecipientsAsync(property.Id, recipients);
        return (property, placement);
    }

    private static async Task<List<ChemicalAlertLog>> EmailLogsAsync()
    {
        await using var db = FreshDb();
        return await db.ChemicalAlertLogs.AsNoTracking().Where(x => x.Channel == ChemicalAlertChannelEnum.Email
                                                       && x.WorkflowState != Constants.WorkflowStates.Removed).ToListAsync();
    }

    [TestCase("2026-10-26T05:59:00Z")] // Monday 06:59 CET: this week's slot has not started
    public async Task BeforeThisWeeksSlot_SendsNothing(string utcNow)
    {
        await ArrangeAsync();
        _time.Now = At(utcNow);

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
        Assert.That(await EmailLogsAsync(), Is.Empty);
    }

    [Test]
    public async Task MondayMorning_OneEmailPerRecipient_AndActionItemsLoggedOnce()
    {
        var (property, placement) = await ArrangeAsync();

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted.Select(e => e.To), Is.EquivalentTo(new[] { "a@example.com", "b@example.com" }));
        Assert.That(_email.Attempted[0].Subject, Is.EqualTo("Kemi: ugentlig oversigt uge 44"));
        Assert.That(_email.Attempted[0].Html,
            Does.Contain(WebUtility.HtmlEncode(ChemicalDigestEmailRenderer.W1Url("4711", property.Id))));
        var log = (await EmailLogsAsync()).Single();
        Assert.That(log.PlacementId, Is.EqualTo(placement.Id));
        Assert.That(log.Threshold, Is.EqualTo(ChemicalAlertThresholdEnum.Passed));
        Assert.That(log.DeadlineKind, Is.EqualTo(ChemicalDeadlineKindEnum.UseAndPossession));
        Assert.That(log.SentAt, Is.EqualTo(RunTime.UtcDateTime));
    }

    [Test]
    public async Task SecondTickSameMonday_SendsNothing()
    {
        await ArrangeAsync();
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));

        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RestartedPod_SameSlot_FreshJobInstanceSendsNothing()
    {
        await ArrangeAsync();
        await CreateJob().RunAsync();

        // A new job object, a new e-mail sender and the same database: a restart or a second pod.
        var otherPodEmail = new FakeChemicalEmailSender();
        await new ChemicalDigestJob(DbHelper, ChemicalHelper, _sdk, otherPodEmail, _time).RunAsync();

        Assert.That(otherPodEmail.Attempted, Is.Empty);
    }

    [Test]
    public async Task NextMonday_PassedDeadlineIsRepeated()
    {
        await ArrangeAsync();
        await CreateJob().RunAsync();

        _time.Now = RunTime.AddDays(7);
        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(4));
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task SummerTime_SlotIsFiveUtc()
    {
        await ArrangeAsync();

        _time.Now = At("2026-10-19T04:50:00Z"); // Monday 06:50 CEST
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Is.Empty);

        _time.Now = At("2026-10-19T05:10:00Z"); // Monday 07:10 CEST
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task OnlyNewRegistrations_NoEmailAndNoLog()
    {
        await ArrangeAsync(possession: FarAway, registeredAt: RunTime.UtcDateTime.AddDays(-1));

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
        Assert.That(await EmailLogsAsync(), Is.Empty);
    }

    [Test]
    public async Task RegistrationExactlyAtThePreviousSlot_WithUnspecifiedKind_IsListedAsNew()
    {
        // MariaDB returns DateTimeKind.Unspecified. The previous slot is 2026-10-19 05:00 UTC
        // (CEST), one hour earlier than a naive 7 x 24 h subtraction would give.
        var (property, _) = await ArrangeAsync();
        var location = await CreateLocationAsync(property.Id, "Lade");
        var basta = await AddChemicalAsync("Basta", possession: FarAway);
        await CreatePlacementAsync(location.Id, basta, registeredAt: new DateTime(2026, 10, 19, 5, 0, 0, DateTimeKind.Unspecified));
        var tooEarly = await AddChemicalAsync("Gammelt Middel", possession: FarAway);
        await CreatePlacementAsync(location.Id, tooEarly, registeredAt: new DateTime(2026, 10, 19, 4, 59, 59, DateTimeKind.Unspecified));

        await CreateJob().RunAsync();

        var text = _email.Attempted[0].PlainText;
        Assert.That(text, Does.Contain("Nye registreringer denne uge"));
        Assert.That(text, Does.Contain("Basta"));
        Assert.That(text, Does.Not.Contain("Gammelt Middel"));
    }

    [Test]
    public async Task RecipientOnTwoProperties_GetsOneEmailWithBoth()
    {
        await ArrangeAsync(recipients: "a@example.com", propertyName: "Gården");
        await ArrangeAsync(recipients: "A@EXAMPLE.com; b@example.com", propertyName: "Nordgården");

        await CreateJob().RunAsync();

        var toA = _email.Attempted.Single(e => e.To == "a@example.com");
        Assert.That(toA.PlainText, Does.Contain("Gården"));
        Assert.That(toA.PlainText, Does.Contain("Nordgården"));
        Assert.That(_email.Attempted.Single(e => e.To == "b@example.com").PlainText, Does.Not.Contain("\nGården"));
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(2));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-an-address")]
    public async Task NoUsableRecipients_SendsNothing(string recipients)
    {
        await ArrangeAsync(recipients: recipients);

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
        Assert.That(await EmailLogsAsync(), Is.Empty);
    }

    [Test]
    public async Task OneRecipientFails_TheRestIsLogged_AndNotResent()
    {
        await ArrangeAsync();
        _email.Fails = e => e.To == "b@example.com";

        await CreateJob().RunAsync();
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(1));

        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2), "the weekly guard stops a resend to the recipient who got it");
    }

    [Test]
    public async Task EveryRecipientFails_NothingLogged_NextTickRetries()
    {
        await ArrangeAsync();
        _email.Fails = _ => true;

        await CreateJob().RunAsync();
        Assert.That(await EmailLogsAsync(), Is.Empty);

        _email.Fails = _ => false;
        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(4));
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task MissingSendGridKey_SendsNothing()
    {
        await ArrangeAsync();
        _email.Key = null;

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
        Assert.That(await EmailLogsAsync(), Is.Empty);
    }

    [Test]
    public async Task RemovedPlacement_IsNotListed()
    {
        var (_, placement) = await ArrangeAsync();
        await ClosePlacementAsync(placement, ChemicalRemovalReasonEnum.Disposed);

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
    }

    [Test]
    public async Task PushLogs_DoNotSuppressTheDigest()
    {
        var (_, placement) = await ArrangeAsync();
        await new ChemicalAlertLog
        {
            PlacementId = placement.Id, Threshold = ChemicalAlertThresholdEnum.Passed,
            DeadlineKind = ChemicalDeadlineKindEnum.UseAndPossession, Channel = ChemicalAlertChannelEnum.Push,
            SentAt = RunTime.UtcDateTime.AddMinutes(-5), CreatedByUserId = 0, UpdatedByUserId = 0,
        }.Create(Db);

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task WednesdayWithoutALogForMondaysSlot_SendsOnce_ThenNothing()
    {
        // The pod was down on Monday: the most recent slot (Mon 2026-10-26 06:00 UTC) is sent late, once.
        await ArrangeAsync(possession: FarAway, registeredAt: At("2026-10-22T08:00:00Z").UtcDateTime);
        await ArrangeAsync(propertyName: "Nordgården");
        _time.Now = At("2026-10-28T10:00:00Z");

        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));

        _time.Now = At("2026-10-28T11:00:00Z");
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CatchUp_KeepsTheNewRegistrationWindowOfThatSlot()
    {
        var (property, _) = await ArrangeAsync();
        var location = await CreateLocationAsync(property.Id, "Lade");
        var inWindow = await AddChemicalAsync("Basta", possession: FarAway);
        await CreatePlacementAsync(location.Id, inWindow, registeredAt: At("2026-10-22T08:00:00Z").UtcDateTime);
        var afterSlot = await AddChemicalAsync("Senere Middel", possession: FarAway);
        await CreatePlacementAsync(location.Id, afterSlot, registeredAt: At("2026-10-27T08:00:00Z").UtcDateTime);
        _time.Now = At("2026-10-28T10:00:00Z");

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted[0].PlainText, Does.Contain("Basta"));
        Assert.That(_email.Attempted[0].PlainText, Does.Not.Contain("Senere Middel"));
    }

    [Test]
    public async Task TheClaimIsWrittenBeforeTheFirstSend()
    {
        await ArrangeAsync();
        var claimsAtFirstSend = -1;
        _email.BeforeSend = async _ =>
        {
            if (claimsAtFirstSend < 0)
            {
                claimsAtFirstSend = (await EmailLogsAsync()).Count;
            }
        };

        await CreateJob().RunAsync();

        Assert.That(claimsAtFirstSend, Is.EqualTo(1));
    }

    [Test]
    public async Task SecondJobInstanceStartedMidRun_SendsNothing()
    {
        // A restarted or second pod ticking while the first is between its claim and its last send.
        await ArrangeAsync();
        var secondPod = new FakeChemicalEmailSender();
        var started = false;
        _email.BeforeSend = async _ =>
        {
            if (!started)
            {
                started = true;
                await new ChemicalDigestJob(DbHelper, ChemicalHelper, _sdk, secondPod, _time).RunAsync();
            }
        };

        await CreateJob().RunAsync();

        Assert.That(secondPod.Attempted, Is.Empty);
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TimeoutOnOneRecipient_DoesNotStopTheOthers_AndKeepsTheClaim()
    {
        await ArrangeAsync();
        _email.TimesOut = e => e.To == "a@example.com";

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted.Select(e => e.To), Is.EqualTo(new[] { "a@example.com", "b@example.com" }));
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EveryRecipientTimesOut_ReleasesTheClaim_AndTheNextTickSends()
    {
        await ArrangeAsync();
        _email.TimesOut = _ => true;

        await CreateJob().RunAsync();
        Assert.That(await EmailLogsAsync(), Is.Empty);

        _email.TimesOut = _ => false;
        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(4));
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FailureWhileClaiming_LeavesNoPartialClaim_AndSendsNothing()
    {
        var (property, _) = await ArrangeAsync();
        var location = await CreateLocationAsync(property.Id, "Lade");
        var second = await AddChemicalAsync("Basta", possession: AlreadyPassed);
        await CreatePlacementAsync(location.Id, second, registeredAt: LongAgo);
        var table = Db.Model.FindEntityType(typeof(ChemicalAlertLog))!.GetTableName();
        var email = (int)ChemicalAlertChannelEnum.Email;
        await Db.Database.ExecuteSqlRawAsync(
            $"CREATE TRIGGER `chemical_test_fail_second_claim` BEFORE INSERT ON `{table}` FOR EACH ROW " +
            $"BEGIN IF (SELECT COUNT(*) FROM `{table}` WHERE `Channel` = {email}) >= 1 THEN " +
            "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'test: second claim row fails'; END IF; END");
        try
        {
            Assert.That(async () => await CreateJob().RunAsync(), Throws.Exception);
        }
        finally
        {
            await Db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS `chemical_test_fail_second_claim`");
        }

        Assert.That(_email.Attempted, Is.Empty);
        Assert.That(await EmailLogsAsync(), Is.Empty, "the one row written before the failure is released");

        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2), "the next tick retries");
    }

    [Test]
    public async Task MondayWithNothingToSend_ClosesTheWeek_ALaterDueItemWaitsForNextMonday()
    {
        var (property, _) = await ArrangeAsync(possession: FarAway);

        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Is.Empty);

        // Wednesday: an item has become due, but this week's evaluation already happened.
        var location = await CreateLocationAsync(property.Id, "Lade");
        var due = await AddChemicalAsync("Basta", possession: AlreadyPassed);
        await CreatePlacementAsync(location.Id, due, registeredAt: LongAgo);
        _time.Now = At("2026-10-28T10:00:00Z");
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Is.Empty);

        _time.Now = At("2026-11-02T06:10:00Z"); // next Monday 07:10 CET
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task MondayWithoutRecipients_ClosesTheWeek()
    {
        var (property, _) = await ArrangeAsync(recipients: "");

        await CreateJob().RunAsync();
        await SetDigestRecipientsAsync(property.Id, "a@example.com");
        _time.Now = At("2026-10-28T10:00:00Z");
        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
    }

    [Test]
    public async Task DowntimeOnMonday_TuesdayEvaluatesAndSendsOnce()
    {
        await ArrangeAsync();
        _time.Now = At("2026-10-27T08:00:00Z");

        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));

        _time.Now = At("2026-10-27T09:00:00Z");
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task MissingKeyOrCustomerNo_DoesNotCloseTheWeek()
    {
        await ArrangeAsync();
        _email.Key = null;
        await CreateJob().RunAsync();
        _email.Key = "SG.test";
        _sdk.CustomerNo = "";
        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Is.Empty);

        _sdk.CustomerNo = "4711";
        _time.Now = RunTime.AddHours(2);
        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task SendGridRejectsTheKey_StopsAtTheFirstRecipient_AndReleasesTheClaim()
    {
        await ArrangeAsync();
        _email.AuthFails = _ => true;

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(1), "a systemic failure must not be retried once per recipient");
        Assert.That(await EmailLogsAsync(), Is.Empty);

        _email.AuthFails = _ => false;
        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Has.Count.EqualTo(3));
        Assert.That(await EmailLogsAsync(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task TheKeyIsReadOnceAndHandedToEverySend()
    {
        await ArrangeAsync();

        await CreateJob().RunAsync();

        Assert.That(_email.KeyReads, Is.EqualTo(1));
        Assert.That(_email.KeysUsed, Is.EqualTo(new[] { "SG.test", "SG.test" }));
    }

    [Test]
    public async Task NothingToSend_TheKeyIsNotRead()
    {
        await ArrangeAsync(possession: FarAway, registeredAt: RunTime.UtcDateTime.AddDays(-1));

        await CreateJob().RunAsync();

        Assert.That(_email.KeyReads, Is.EqualTo(0));
    }

    [Test]
    public async Task EmptyCustomerNo_SendsNothing_AndLogsNothing()
    {
        await ArrangeAsync();
        _sdk.CustomerNo = "";

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
        Assert.That(await EmailLogsAsync(), Is.Empty);

        _sdk.CustomerNo = "4711";
        _time.Now = RunTime.AddHours(1);
        await CreateJob().RunAsync();
        Assert.That(_email.Attempted, Has.Count.EqualTo(2), "the next tick retries once the setting is fixed");
    }

    [Test]
    public async Task MissingCustomerNoSetting_SendsNothing()
    {
        await ArrangeAsync();
        _sdk.CustomerNoThrows = true;

        await CreateJob().RunAsync();

        Assert.That(_email.Attempted, Is.Empty);
        Assert.That(await EmailLogsAsync(), Is.Empty);
    }
}
