namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using System.Collections.Generic;
using System.Linq;
using Microting.EformBackendConfigurationBase.Infrastructure.Enum;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

[TestFixture]
public class ChemicalAlertPlannerTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static ChemicalAlertPlacement Placement(int id = 1, int? observed = 5, params int[] ancestors) =>
        new(id, 10, 100, observed, new[] { id }.Concat(ancestors).ToList());

    private static ChemicalRegisterFacts Facts(int? status = 5, DateTime? sales = null, DateTime? possession = null) =>
        new(100, "Roundup Flex", "1-234", status, sales, possession);

    private static ChemicalAlertPlan Plan(
        ChemicalAlertPlacement placement, ChemicalRegisterFacts facts, params ChemicalAlertLogKey[] sentLog) =>
        ChemicalAlertPlanner.Plan([placement], new Dictionary<int, ChemicalRegisterFacts> { [facts.ChemicalId] = facts },
            sentLog, Today);

    private static ChemicalAlertLogKey Logged(int placementId, ChemicalAlertThresholdEnum threshold,
        ChemicalDeadlineKindEnum? kind = null, int? status = null) => new(placementId, threshold, kind, status);

    [Test]
    public void Plan_DeadlineFarAway_NothingDue()
    {
        var plan = Plan(Placement(), Facts(possession: Utc(2028, 1, 1)));

        Assert.That(plan.DueAlerts, Is.Empty);
        Assert.That(plan.ObservedStatusUpdates, Is.Empty);
    }

    [Test]
    public void Plan_SixMonthsReached_OneAlertWithKindAndDate()
    {
        var alert = Plan(Placement(), Facts(possession: Utc(2027, 3, 15))).DueAlerts.Single();

        Assert.That(alert, Is.EqualTo(new ChemicalDueAlert(1, 10, 100, "Roundup Flex",
            ChemicalAlertThresholdEnum.SixMonths, ChemicalDeadlineKindEnum.UseAndPossession, null, new DateOnly(2027, 3, 15))));
    }

    [Test]
    public void Plan_SameThresholdAlreadyLogged_NothingDue()
    {
        var plan = Plan(Placement(), Facts(possession: Utc(2027, 3, 15)),
            Logged(1, ChemicalAlertThresholdEnum.SixMonths, ChemicalDeadlineKindEnum.UseAndPossession));

        Assert.That(plan.DueAlerts, Is.Empty);
    }

    [Test]
    public void Plan_NextThresholdReached_AfterSixMonthsWasLogged()
    {
        var alert = Plan(Placement(), Facts(possession: Utc(2026, 10, 20)),
            Logged(1, ChemicalAlertThresholdEnum.SixMonths, ChemicalDeadlineKindEnum.UseAndPossession)).DueAlerts.Single();

        Assert.That(alert.Threshold, Is.EqualTo(ChemicalAlertThresholdEnum.OneMonth));
    }

    [Test]
    public void Plan_PassedOnFirstSight_OnlyTheMostSevereThreshold()
    {
        var alerts = Plan(Placement(), Facts(sales: Utc(2026, 9, 1))).DueAlerts;

        Assert.That(alerts.Select(a => (a.Threshold, a.DeadlineKind)),
            Is.EqualTo(new[] { (ChemicalAlertThresholdEnum.Passed, (ChemicalDeadlineKindEnum?)ChemicalDeadlineKindEnum.Sales) }));
    }

    [Test]
    public void Plan_MoreSevereAlreadyLogged_LesserThresholdNeverFires()
    {
        var plan = Plan(Placement(), Facts(sales: Utc(2026, 10, 20)),
            Logged(1, ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales));

        Assert.That(plan.DueAlerts, Is.Empty);
    }

    [Test]
    public void Plan_KindsAreTrackedSeparately()
    {
        var alerts = Plan(Placement(), Facts(sales: Utc(2026, 10, 20), possession: Utc(2027, 3, 15)),
            Logged(1, ChemicalAlertThresholdEnum.OneMonth, ChemicalDeadlineKindEnum.Sales)).DueAlerts;

        Assert.That(alerts.Select(a => (a.Threshold, a.DeadlineKind)), Is.EqualTo(new[]
        {
            (ChemicalAlertThresholdEnum.SixMonths, (ChemicalDeadlineKindEnum?)ChemicalDeadlineKindEnum.UseAndPossession)
        }));
    }

    [Test]
    public void Plan_BothKindsReached_UseAndPossessionFirst()
    {
        var alerts = Plan(Placement(), Facts(sales: Utc(2026, 10, 20), possession: Utc(2027, 3, 15))).DueAlerts;

        Assert.That(alerts.Select(a => a.DeadlineKind), Is.EqualTo(new ChemicalDeadlineKindEnum?[]
        {
            ChemicalDeadlineKindEnum.UseAndPossession, ChemicalDeadlineKindEnum.Sales
        }));
    }

    [Test]
    public void Plan_LogOnAncestor_SuppressesAlertOnMovedPlacement()
    {
        var moved = Placement(2, 5, 1); // placement 2 was moved from placement 1

        var plan = Plan(moved, Facts(sales: Utc(2026, 9, 1)),
            Logged(1, ChemicalAlertThresholdEnum.Passed, ChemicalDeadlineKindEnum.Sales));

        Assert.That(plan.DueAlerts, Is.Empty);
    }

    [Test]
    public void Plan_StatusChangedToDeregistered_AlertDue_ObservedStatusNotYetUpdated()
    {
        var plan = Plan(Placement(observed: 5), Facts(status: 6));

        Assert.That(plan.DueAlerts.Single(), Is.EqualTo(new ChemicalDueAlert(1, 10, 100, "Roundup Flex",
            ChemicalAlertThresholdEnum.StatusChanged, null, 6, null)));
        Assert.That(plan.ObservedStatusUpdates, Is.Empty, "ObservedStatus is written with the alert log (decision 5)");
    }

    [Test]
    public void Plan_StatusUnchanged_Nothing()
    {
        var plan = Plan(Placement(observed: 7), Facts(status: 7));

        Assert.That(plan.DueAlerts, Is.Empty);
        Assert.That(plan.ObservedStatusUpdates, Is.Empty);
    }

    [Test]
    public void Plan_StatusBackToApproved_SilentUpdate()
    {
        var plan = Plan(Placement(observed: 6), Facts(status: 5));

        Assert.That(plan.DueAlerts, Is.Empty);
        Assert.That(plan.ObservedStatusUpdates, Is.EqualTo(new Dictionary<int, int?> { [1] = 5 }));
    }

    [Test]
    public void Plan_NoObservedBaseline_StoresStatusWithoutAlert()
    {
        var plan = Plan(Placement(observed: null), Facts(status: 6));

        Assert.That(plan.DueAlerts, Is.Empty);
        Assert.That(plan.ObservedStatusUpdates, Is.EqualTo(new Dictionary<int, int?> { [1] = 6 }));
    }

    [Test]
    public void Plan_StatusAlertAlreadyLogged_SilentUpdate()
    {
        var plan = Plan(Placement(observed: 5), Facts(status: 6),
            Logged(1, ChemicalAlertThresholdEnum.StatusChanged, status: 6));

        Assert.That(plan.DueAlerts, Is.Empty);
        Assert.That(plan.ObservedStatusUpdates, Is.EqualTo(new Dictionary<int, int?> { [1] = 6 }));
    }

    [Test]
    public void Plan_ChemicalMissingFromRegister_Skipped()
    {
        var plan = ChemicalAlertPlanner.Plan([Placement()], new Dictionary<int, ChemicalRegisterFacts>(), [], Today);

        Assert.That(plan.DueAlerts, Is.Empty);
        Assert.That(plan.ObservedStatusUpdates, Is.Empty);
    }

    [Test]
    public void Lineage_WalksMovedFromChain_SelfFirst()
    {
        var movedFrom = new Dictionary<int, int?> { [3] = 2, [2] = 1 };

        Assert.That(ChemicalAlertPlanner.Lineage(3, movedFrom), Is.EqualTo(new[] { 3, 2, 1 }));
        Assert.That(ChemicalAlertPlanner.Lineage(9, movedFrom), Is.EqualTo(new[] { 9 }));
    }

    [Test]
    public void Lineage_CorruptCycle_Terminates()
    {
        var movedFrom = new Dictionary<int, int?> { [1] = 2, [2] = 1 };

        Assert.That(ChemicalAlertPlanner.Lineage(1, movedFrom), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void RegisterFacts_Deadline_SelectsByKind()
    {
        var facts = Facts(sales: Utc(2026, 10, 20), possession: Utc(2027, 3, 15));

        Assert.That(facts.Deadline(ChemicalDeadlineKindEnum.Sales), Is.EqualTo(Utc(2026, 10, 20)));
        Assert.That(facts.Deadline(ChemicalDeadlineKindEnum.UseAndPossession), Is.EqualTo(Utc(2027, 3, 15)));
    }

    [Test]
    public void RegisterFacts_DeadlineKinds_UseAndPossessionFirst()
    {
        Assert.That(ChemicalRegisterFacts.DeadlineKinds, Is.EqualTo(new[]
        {
            ChemicalDeadlineKindEnum.UseAndPossession, ChemicalDeadlineKindEnum.Sales
        }));
    }
}
