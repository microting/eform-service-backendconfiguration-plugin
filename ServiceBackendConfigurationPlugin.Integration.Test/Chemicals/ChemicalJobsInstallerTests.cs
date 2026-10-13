namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using System;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Helpers;
using ServiceBackendConfigurationPlugin.Installers;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

/// <summary>Core.cs resolves the chemical jobs on every hourly tick; this proves the container can build them.</summary>
[TestFixture]
public class ChemicalJobsInstallerTests
{
    [Test]
    public void Install_ResolvesTheChemicalJobs_WithoutOpeningAnyDatabase()
    {
        using var container = new WindsorContainer();
        container.Register(
            Component.For<BackendConfigurationDbContextHelper>().Instance(new BackendConfigurationDbContextHelper("Server=unused")),
            Component.For<ChemicalDbContextHelper>().Instance(new ChemicalDbContextHelper("Server=unused")));

        container.Install(new ChemicalJobsInstaller(
            () => throw new InvalidOperationException("the SDK database must not be opened while resolving"),
            () => throw new InvalidOperationException("the SendGrid key must not be read while resolving")));

        Assert.That(container.Resolve<ChemicalAlertJob>(), Is.Not.Null);
        Assert.That(container.Resolve<ChemicalDigestJob>(), Is.Not.Null);
        Assert.That(container.Resolve<TimeProvider>(), Is.SameAs(TimeProvider.System));
    }
}
