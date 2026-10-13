using System;
using Castle.MicroKernel.Registration;
using Castle.MicroKernel.SubSystems.Configuration;
using Castle.Windsor;
using Microting.eForm.Infrastructure;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;
using ServiceBackendConfigurationPlugin.Scheduler.Jobs;

namespace ServiceBackendConfigurationPlugin.Installers;

/// <summary>Registers the flutter-chemistry alert jobs and their collaborators.</summary>
public sealed class ChemicalJobsInstaller(
    Func<MicrotingDbContext> sdkDbContextFactory, Func<string> readSendGridKey) : IWindsorInstaller
{
    public void Install(IWindsorContainer container, IConfigurationStore store)
    {
        container.Register(
            Component.For<TimeProvider>().Instance(TimeProvider.System),
            Component.For<IChemicalSdkDirectory>().Instance(new ChemicalSdkDirectory(sdkDbContextFactory)),
            Component.For<IChemicalPushSender>().ImplementedBy<FirebaseChemicalPushSender>(),
            Component.For<IChemicalEmailSender>().Instance(
                new SendGridChemicalEmailSender(readSendGridKey, SendGridChemicalEmailSender.SendWithSendGridAsync)),
            Component.For<ChemicalAlertJob>(),
            Component.For<ChemicalDigestJob>());
    }
}
