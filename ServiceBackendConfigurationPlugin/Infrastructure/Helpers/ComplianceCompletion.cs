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

using Microting.EformBackendConfigurationBase.Infrastructure.Data.Entities;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Helpers;

/// <summary>
/// A task assigned to several workers gets one SDK case per worker, but its
/// <see cref="Compliance"/> stores only one of them. When another worker completes
/// the task, the stored case is retracted — so before the compliance is soft-removed
/// it must point at the case that was actually completed, or every reader of the
/// compliance (Compliance report, calendar, gRPC event list) sees "not completed"
/// (eform-service-backendconfiguration-plugin #588, eform-backendconfiguration-plugin #1371).
/// </summary>
public static class ComplianceCompletion
{
    /// <summary>
    /// Points <paramref name="compliance"/> at <paramref name="completedSite"/>'s case
    /// unless its stored case is completed itself (<paramref name="storedCaseCompleted"/>):
    /// then the completion PROCESSED first keeps the occurrence. That is processing order,
    /// not completion time — with near-simultaneous completions it can differ from the
    /// earliest-completion rule the backend-configuration readers apply.
    /// </summary>
    public static void PointAtCompletedCase(
        Compliance compliance, PlanningCaseSite completedSite, bool storedCaseCompleted)
    {
        if (storedCaseCompleted)
        {
            return;
        }

        compliance.MicrotingSdkCaseId = completedSite.MicrotingSdkCaseId;

        // The column is named PlanningCaseSiteId but has always held the PlanningCaseId
        // (see EformParsedByServerHandler). A compliance found by planning + deadline may
        // never have had it set; fill it, but never rewrite an existing link.
        if (compliance.PlanningCaseSiteId == 0)
        {
            compliance.PlanningCaseSiteId = completedSite.PlanningCaseId;
        }
    }
}
