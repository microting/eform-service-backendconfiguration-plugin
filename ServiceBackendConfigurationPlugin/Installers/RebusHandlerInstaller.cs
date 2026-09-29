/*
The MIT License (MIT)

Copyright (c) 2007 - 2022 Microting A/S

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


using Castle.MicroKernel.Registration;
using Castle.MicroKernel.SubSystems.Configuration;
using Castle.Windsor;
using Rebus.Handlers;
using ServiceBackendConfigurationPlugin.Handlers;
using ServiceBackendConfigurationPlugin.Messages;

namespace ServiceBackendConfigurationPlugin.Installers;

public sealed class RebusHandlerInstaller : IWindsorInstaller
{
    public void Install(IWindsorContainer container, IConfigurationStore store)
    {
        container.Register(Component.For<IHandleMessages<eFormCompleted>>().ImplementedBy<EFormCompletedHandler>().LifestyleTransient());
        container.Register(Component.For<IHandleMessages<EformParsedByServer>>().ImplementedBy<EformParsedByServerHandler>().LifestyleTransient());
        container.Register(Component.For<IHandleMessages<eFormRetrieved>>().ImplementedBy<EformRetrievedHandler>().LifestyleTransient());
        container.Register(Component.For<IHandleMessages<WorkOrderCaseCompleted>>().ImplementedBy<WorkOrderCaseCompletedHandler>().LifestyleTransient());
        container.Register(Component.For<IHandleMessages<OldWorkOrderCaseCompleted>>().ImplementedBy<OldWorkOrderCaseCompletedHandler>().LifestyleTransient());
        container.Register(Component.For<IHandleMessages<MorningTourCaseCompleted>>().ImplementedBy<MorningTourCaseCompletedHandler>().LifestyleTransient());
        container.Register(Component.For<IHandleMessages<PoolHourCaseCompleted>>().ImplementedBy<PoolHourCaseCompletedHandler>().LifestyleTransient());
        container.Register(Component.For<IHandleMessages<FloatingLayerCaseCompleted>>().ImplementedBy<FloatingLayerCaseCompletedHandler>().LifestyleTransient());
    }
}