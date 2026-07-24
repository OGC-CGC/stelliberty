using System.Diagnostics;
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Subscriptions;

namespace SBproxy.Desktop.Services;

public sealed class DesktopSubscriptionFileOpener(Func<string, string> resolveContentPath) : ISubscriptionFileOpener
{
    public void OpenSubscriptionFile(string subscriptionId)
    {
        var path = resolveContentPath(subscriptionId);
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
            AppLogger.Info($"Subscription file was handed off to the system shell: {subscriptionId}");
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, $"Subscription file open failed: {subscriptionId}");
        }
    }
}
