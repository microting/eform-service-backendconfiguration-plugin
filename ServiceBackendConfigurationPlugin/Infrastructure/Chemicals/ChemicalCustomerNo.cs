using System;
using System.Threading.Tasks;
using Sentry;

namespace ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

public static class ChemicalCustomerNo
{
    /// <summary>
    /// The tenant's customerNo, or null when the SDK setting is missing or empty
    /// (reported to Sentry under <paramref name="emptyFingerprint"/>; the caller then sends
    /// nothing and retries on a later tick). The directory throws on a missing setting.
    /// </summary>
    public static async Task<string> TryReadAsync(IChemicalSdkDirectory sdkDirectory, string jobName, string emptyFingerprint)
    {
        try
        {
            var customerNo = await sdkDirectory.CustomerNoAsync();
            if (!string.IsNullOrEmpty(customerNo))
            {
                return customerNo;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail: {jobName} - customerNo unavailable: {e.Message}");
            SentrySdk.CaptureException(e);
            return null;
        }

        var empty = $"{jobName} - the SDK setting customerNo is empty; sending nothing this run.";
        Console.WriteLine($"fail: {empty}");
        SentrySdk.CaptureMessage(empty, scope => scope.SetFingerprint([emptyFingerprint]), SentryLevel.Error);
        return null;
    }
}
