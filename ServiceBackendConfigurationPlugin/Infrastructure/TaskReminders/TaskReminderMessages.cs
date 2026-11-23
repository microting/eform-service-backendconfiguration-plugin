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
using System.Globalization;
using FirebaseAdmin.Messaging;
using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;

namespace ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

/// <summary>
/// The FCM messages of the task reminder contract (package 12/13). Ids are strings, as on the hub and
/// gRPC wire. Only ids, the planned time and the row's already rendered title and body leave the
/// server; no names of people, no comments, no field values.
/// </summary>
public static class TaskReminderMessages
{
    public const string TaskDueType = "task_due";
    public const string TaskWithdrawnType = "task_withdrawn";
    public const string ContractVersion = "1";

    /// <summary>Android notification channel and iOS thread of task reminders.</summary>
    public const string Channel = "tasks";

    /// <summary>
    /// The tag that ties a reminder and its withdraw together on the phone (Android notification tag,
    /// APNs collapse id, data "tag"): <c>task:&lt;event_id&gt;:&lt;plan_day_key&gt;:&lt;planned start UTC
    /// yyyyMMddTHHmmZ&gt;</c>. Schedule-specific: after a reschedule the outbox has a row per planned
    /// start, and a late withdraw of the old start must not remove the replacement's notification.
    /// Worst case 41 bytes (event id 2147483647), within APNs' 64-byte collapse-id limit.
    /// </summary>
    public static string Tag(TaskNotification row) =>
        $"task:{row.AreaRulePlanningId.ToString(CultureInfo.InvariantCulture)}:{row.PlanDayKey}:"
        + row.PlannedStartUtc.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture);

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Epoch(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>Transport lifetime until <paramref name="end"/>, at least one second (FCM's TTL).</summary>
    private static TimeSpan Until(DateTime end, DateTime nowUtc)
    {
        var left = end - nowUtc;
        return left < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
    }

    /// <summary>The visible "task starts soon" reminder for one device token.</summary>
    public static Message TaskDue(TaskNotification row, string fcmToken, DateTime nowUtc)
    {
        var tag = Tag(row);
        var data = new Dictionary<string, string>
        {
            ["type"] = TaskDueType,
            ["v"] = ContractVersion,
            ["event_id"] = row.AreaRulePlanningId.ToString(CultureInfo.InvariantCulture),
            ["plan_day_key"] = row.PlanDayKey,
            ["property_id"] = row.PropertyId.ToString(CultureInfo.InvariantCulture),
            ["planned_start_utc"] = Iso(row.PlannedStartUtc),
            ["tag"] = tag
        };
        if (row.ComplianceId is { } complianceId)
        {
            data["compliance_id"] = complianceId.ToString(CultureInfo.InvariantCulture);
        }

        return new Message
        {
            // Message.Token is obsolete in FirebaseAdmin in favour of Fid, which is a different wire
            // field (installation-id targeting); FCM registration tokens go in "token" (as AdhocReminderJob).
#pragma warning disable CS0618
            Token = fcmToken,
#pragma warning restore CS0618
            Notification = new Notification { Title = row.Title, Body = row.Body },
            Data = data,
            // Expires at the planned start: an offline phone must not show "starts soon" afterwards
            // (FCM keeps Android messages up to four weeks by default).
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = Until(row.PlannedStartUtc, nowUtc),
                Notification = new AndroidNotification { ChannelId = Channel, Tag = tag }
            },
            Apns = new ApnsConfig
            {
                Headers = new Dictionary<string, string>
                {
                    ["apns-collapse-id"] = tag,
                    ["apns-expiration"] = Epoch(row.PlannedStartUtc)
                },
                Aps = new Aps { ThreadId = Channel }
            }
        };
    }

    /// <summary>
    /// The silent "remove that reminder" message: data only, so nothing is shown; the app cancels the
    /// notification with this tag. APNs needs content-available, push type background and priority 5
    /// for a silent push; iOS throttles these and does not deliver them to a force-quit app, so a
    /// withdraw is best effort there.
    /// </summary>
    public static Message TaskWithdrawn(TaskNotification row, string fcmToken, DateTime nowUtc)
    {
        var tag = Tag(row);
        return new Message
        {
#pragma warning disable CS0618
            Token = fcmToken,
#pragma warning restore CS0618
            Data = new Dictionary<string, string>
            {
                ["type"] = TaskWithdrawnType,
                ["v"] = ContractVersion,
                ["event_id"] = row.AreaRulePlanningId.ToString(CultureInfo.InvariantCulture),
                ["plan_day_key"] = row.PlanDayKey,
                ["planned_start_utc"] = Iso(row.PlannedStartUtc),
                ["tag"] = tag
            },
            // Expires with the withdraw's retry cut-off, 24 h after the planned start.
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = Until(row.PlannedStartUtc + TaskReminderDeliverer.WithdrawWindow, nowUtc)
            },
            Apns = new ApnsConfig
            {
                Headers = new Dictionary<string, string>
                {
                    ["apns-push-type"] = "background",
                    ["apns-priority"] = "5",
                    ["apns-expiration"] = Epoch(row.PlannedStartUtc + TaskReminderDeliverer.WithdrawWindow)
                },
                Aps = new Aps { ContentAvailable = true }
            }
        };
    }
}
