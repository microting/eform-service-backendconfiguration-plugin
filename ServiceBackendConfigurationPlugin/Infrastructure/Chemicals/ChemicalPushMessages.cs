using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FirebaseAdmin.Messaging;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

public sealed record ChemicalPushContent(string Title, string Body, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Builds chemistry pushes. customerNo (the tenant's SDK customerNo) lets the app drop a tap from another customer. The data keys and values are Plan D Contract C3:
/// flutter-chemistry's push router depends on them.
/// </summary>
public static class ChemicalPushMessages
{
    public const string TypeAlert = "chemical_alert";
    public const string TypeSummary = "chemical_alert_summary";

    /// <summary>Must match the Android notification channel flutter-chemistry creates.</summary>
    public const string AndroidChannelId = "chemical_alerts";

    /// <summary>iOS notification category flutter-chemistry registers (action MARK_REMOVED).</summary>
    public const string ApnsCategory = "CHEMICAL_ALERT";

    /// <summary>More alerts than this on one property in one run collapse into one summary push (decision 6).</summary>
    public const int MaxIndividualAlertsPerProperty = 3;

    public static IReadOnlyList<ChemicalPushContent> Compose(
        int propertyId, string propertyName, IReadOnlyList<ChemicalDueAlert> alerts, string languageCode,
        string customerNo)
    {
        // The app drops a tap whose customerNo differs from its session; a push without one would route in any session.
        ArgumentException.ThrowIfNullOrEmpty(customerNo);
        var texts = ChemicalAlertTexts.For(languageCode);
        if (alerts.Count > MaxIndividualAlertsPerProperty)
        {
            return [Summary(propertyId, propertyName, alerts.Count, texts, customerNo)];
        }

        return alerts.Select(alert => Individual(alert, propertyName, texts, customerNo)).ToList();
    }

    public static Message ToFcmMessage(ChemicalPushContent content, string fcmToken) => new()
    {
        // Message.Token is obsolete in FirebaseAdmin 3.x in favour of Fid, which
        // serialises to a DIFFERENT wire field. DeviceTokens holds FCM
        // registration tokens, which FCM v1 accepts only via "token" (same
        // reasoning as AdhocReminderJob).
#pragma warning disable CS0618
        Token = fcmToken,
#pragma warning restore CS0618
        Notification = new Notification { Title = content.Title, Body = content.Body },
        Data = content.Data,
        Android = new AndroidConfig
        {
            Priority = Priority.High,
            Notification = new AndroidNotification { ChannelId = AndroidChannelId },
        },
        // The remove action only makes sense on a single product: a summary gets no category.
        Apns = new ApnsConfig
        {
            Aps = new Aps
            {
                Category = content.Data.TryGetValue("type", out var type) && type == TypeAlert ? ApnsCategory : null,
                Sound = "default",
            },
        },
    };

    private static ChemicalPushContent Individual(
        ChemicalDueAlert alert, string propertyName, ChemicalAlertTexts texts, string customerNo)
    {
        var propertyId = Text(alert.PropertyId);
        var data = new Dictionary<string, string>
        {
            ["type"] = TypeAlert,
            ["propertyId"] = propertyId,
            ["placementId"] = Text(alert.PlacementId),
            ["chemicalId"] = Text(alert.ChemicalId),
            ["threshold"] = ThresholdKey(alert.Threshold),
            ["deadlineKind"] = DeadlineKindKey(alert.DeadlineKind),
            ["status"] = alert.Status is { } status ? Text(status) : string.Empty,
            ["deadline"] = alert.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            ["route"] = $"/chemicals/{alert.ChemicalId}?propertyId={propertyId}",
            ["removeRoute"] = $"/placements/{alert.PlacementId}/remove?propertyId={propertyId}",
            ["customerNo"] = customerNo,
        };
        return new ChemicalPushContent(texts.Title(alert), texts.Body(alert, propertyName), data);
    }

    private static ChemicalPushContent Summary(
        int propertyId, string propertyName, int count, ChemicalAlertTexts texts, string customerNo)
    {
        var id = Text(propertyId);
        var data = new Dictionary<string, string>
        {
            ["type"] = TypeSummary,
            ["propertyId"] = id,
            ["count"] = Text(count),
            ["route"] = $"/inventory?propertyId={id}&filter=requires-action",
            ["customerNo"] = customerNo,
        };
        return new ChemicalPushContent(texts.SummaryTitle(count), propertyName, data);
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ThresholdKey(ChemicalAlertThresholdEnum threshold) => threshold switch
    {
        ChemicalAlertThresholdEnum.SixMonths => "six_months",
        ChemicalAlertThresholdEnum.OneMonth => "one_month",
        ChemicalAlertThresholdEnum.Passed => "passed",
        _ => "status_changed",
    };

    private static string DeadlineKindKey(ChemicalDeadlineKindEnum? kind) => kind switch
    {
        ChemicalDeadlineKindEnum.Sales => "sales",
        ChemicalDeadlineKindEnum.UseAndPossession => "use_and_possession",
        _ => string.Empty,
    };
}
