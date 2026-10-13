#pragma warning disable CS0618 // Message.Token: see ChemicalPushMessages.ToFcmMessage
namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using System.Collections.Generic;
using System.Linq;
using FirebaseAdmin.Messaging;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

/// <summary>Pins Plan D Contract C3; Plan B's push router depends on every key here.</summary>
[TestFixture]
public class ChemicalPushMessagesTests
{
    private static ChemicalDueAlert Deadline(ChemicalAlertThresholdEnum threshold, ChemicalDeadlineKindEnum kind, int placementId = 345) =>
        new(placementId, 12, 6789, "Roundup Flex", threshold, kind, null, new DateOnly(2027, 3, 15));

    private static ChemicalDueAlert Status(int status) =>
        new(345, 12, 6789, "Roundup Flex", ChemicalAlertThresholdEnum.StatusChanged, null, status, null);

    [Test]
    public void Compose_DanishDeadlineAlert_TextsAndPayload()
    {
        var content = ChemicalPushMessages.Compose(12, "Gården",
            [Deadline(ChemicalAlertThresholdEnum.SixMonths, ChemicalDeadlineKindEnum.Sales)], "da", "4711").Single();

        Assert.That(content.Title, Is.EqualTo("Salgsfrist om under 6 måneder"));
        Assert.That(content.Body, Is.EqualTo("Roundup Flex – Gården. Frist: 15-03-2027"));
        Assert.That(content.Data, Is.EqualTo(new Dictionary<string, string>
        {
            ["type"] = "chemical_alert",
            ["propertyId"] = "12",
            ["placementId"] = "345",
            ["chemicalId"] = "6789",
            ["threshold"] = "six_months",
            ["deadlineKind"] = "sales",
            ["status"] = "",
            ["deadline"] = "2027-03-15",
            ["customerNo"] = "4711",
            ["route"] = "/chemicals/6789?propertyId=12",
            ["removeRoute"] = "/placements/345/remove?propertyId=12",
        }));
    }

    [TestCase(ChemicalAlertThresholdEnum.SixMonths, ChemicalDeadlineKindEnum.UseAndPossession, "Brugs- og besiddelsesfrist om under 6 måneder")]
    [TestCase(ChemicalAlertThresholdEnum.OneMonth, ChemicalDeadlineKindEnum.UseAndPossession, "Brugs- og besiddelsesfrist om under 1 måned")]
    [TestCase(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.UseAndPossession, "Brugs- og besiddelsesfristen er overskredet")]
    [TestCase(ChemicalAlertThresholdEnum.OneMonth, ChemicalDeadlineKindEnum.Sales, "Salgsfrist om under 1 måned")]
    [TestCase(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales, "Salgsfristen er overskredet")]
    public void Compose_DanishTitles(ChemicalAlertThresholdEnum threshold, ChemicalDeadlineKindEnum kind, string expected)
    {
        Assert.That(ChemicalPushMessages.Compose(12, "Gården", [Deadline(threshold, kind)], "da", "4711").Single().Title,
            Is.EqualTo(expected));
    }

    [Test]
    public void Compose_PossessionPassed_TellsToDisposeAsHazardousWaste_SalesPassedDoesNot()
    {
        var possession = ChemicalPushMessages.Compose(12, "Gården",
            [Deadline(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.UseAndPossession)], "da", "4711").Single();
        var sales = ChemicalPushMessages.Compose(12, "Gården",
            [Deadline(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales)], "da", "4711").Single();

        Assert.That(possession.Body, Is.EqualTo("Roundup Flex – Gården. Frist: 15-03-2027. Skal bortskaffes som farligt affald."));
        Assert.That(sales.Body, Is.EqualTo("Roundup Flex – Gården. Frist: 15-03-2027"));
        Assert.That(possession.Data["threshold"], Is.EqualTo("passed"));
        Assert.That(possession.Data["deadlineKind"], Is.EqualTo("use_and_possession"));
    }

    [TestCase(6, "Produktet er afmeldt")]
    [TestCase(7, "Produktet er udløbet")]
    public void Compose_StatusChanged_DanishTitleAndPayload(int status, string title)
    {
        var content = ChemicalPushMessages.Compose(12, "Gården", [Status(status)], "da", "4711").Single();

        Assert.That(content.Title, Is.EqualTo(title));
        Assert.That(content.Body, Is.EqualTo("Roundup Flex – Gården"));
        Assert.That(content.Data["customerNo"], Is.EqualTo("4711"));
        Assert.That(content.Data["threshold"], Is.EqualTo("status_changed"));
        Assert.That(content.Data["deadlineKind"], Is.EqualTo(""));
        Assert.That(content.Data["status"], Is.EqualTo(status.ToString()));
        Assert.That(content.Data["deadline"], Is.EqualTo(""));
    }

    [TestCase("en-US")]
    [TestCase("de-DE")]
    [TestCase("")]
    [TestCase(null)]
    public void Compose_NonDanishWorker_GetsEnglish(string languageCode)
    {
        var content = ChemicalPushMessages.Compose(12, "Gården",
            [Deadline(ChemicalAlertThresholdEnum.OneMonth, ChemicalDeadlineKindEnum.Sales)], languageCode, "4711").Single();

        Assert.That(content.Title, Is.EqualTo("Sales deadline in less than 1 month"));
        Assert.That(content.Body, Is.EqualTo("Roundup Flex – Gården. Deadline: 2027-03-15"));
    }

    [Test]
    public void Compose_ThreeAlerts_ThreeIndividualPushes()
    {
        var alerts = Enumerable.Range(1, 3)
            .Select(i => Deadline(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales, placementId: i)).ToList();

        var contents = ChemicalPushMessages.Compose(12, "Gården", alerts, "da", "4711");

        Assert.That(contents.Select(c => c.Data["placementId"]), Is.EqualTo(new[] { "1", "2", "3" }));
    }

    [Test]
    public void Compose_MoreThanThreeAlerts_OneSummary()
    {
        var alerts = Enumerable.Range(1, 4)
            .Select(i => Deadline(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales, placementId: i)).ToList();

        var content = ChemicalPushMessages.Compose(12, "Gården", alerts, "da", "4711").Single();

        Assert.That(content.Title, Is.EqualTo("4 kemikalier kræver handling"));
        Assert.That(content.Body, Is.EqualTo("Gården"));
        Assert.That(content.Data, Is.EqualTo(new Dictionary<string, string>
        {
            ["type"] = "chemical_alert_summary",
            ["propertyId"] = "12",
            ["count"] = "4",
            ["customerNo"] = "4711",
            ["route"] = "/inventory?propertyId=12&filter=requires-action",
        }));
        Assert.That(ChemicalPushMessages.Compose(12, "Gården", alerts, "en-US", "4711").Single().Title,
            Is.EqualTo("4 chemicals need action"));
    }

    [Test]
    public void Compose_NoAlerts_NoPushes()
    {
        Assert.That(ChemicalPushMessages.Compose(12, "Gården", [], "da", "4711"), Is.Empty);
    }

    [Test]
    public void ToFcmMessage_TargetsTheTokenWithChannelCategoryAndData()
    {
        var content = ChemicalPushMessages.Compose(12, "Gården",
            [Deadline(ChemicalAlertThresholdEnum.OneMonth, ChemicalDeadlineKindEnum.Sales)], "da", "4711").Single();

        var message = ChemicalPushMessages.ToFcmMessage(content, "fcm-token-1");

        Assert.That(message.Token, Is.EqualTo("fcm-token-1"));
        Assert.That(message.Notification.Title, Is.EqualTo(content.Title));
        Assert.That(message.Notification.Body, Is.EqualTo(content.Body));
        Assert.That(message.Data, Is.EqualTo(content.Data));
        Assert.That(message.Android.Priority, Is.EqualTo(Priority.High));
        Assert.That(message.Android.Notification.ChannelId, Is.EqualTo("chemical_alerts"));
        Assert.That(message.Apns.Aps.Category, Is.EqualTo("CHEMICAL_ALERT"));
        Assert.That(message.Apns.Aps.Sound, Is.EqualTo("default"));
    }

    [Test]
    public void ToFcmMessage_Summary_HasNoRemoveAction()
    {
        var alerts = Enumerable.Range(1, 4)
            .Select(i => Deadline(ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales, placementId: i)).ToList();
        var content = ChemicalPushMessages.Compose(12, "Gården", alerts, "da", "4711").Single();

        var message = ChemicalPushMessages.ToFcmMessage(content, "fcm-token-1");

        Assert.That(message.Apns.Aps.Category, Is.Null);
        Assert.That(message.Apns.Aps.Sound, Is.EqualTo("default"));
        Assert.That(message.Android.Notification.ChannelId, Is.EqualTo("chemical_alerts"));
    }

    [TestCase("")]
    [TestCase(null)]
    public void Compose_WithoutCustomerNo_Throws(string customerNo)
    {
        Assert.That(() => ChemicalPushMessages.Compose(12, "Gården",
                [Deadline(ChemicalAlertThresholdEnum.OneMonth, ChemicalDeadlineKindEnum.Sales)], "da", customerNo),
            Throws.InstanceOf<ArgumentException>());
    }
}
