using SBproxy.Application.Proxies;
using SBproxy.Domain.Proxies;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;

namespace SBproxy.Infrastructure.Proxies;

public sealed class FileRuntimeProxyConfigSource(string runtimeDirectory, ISubscriptionSelectionStore selectionStore) : IProxyConfigSource
{
    public string ReadRuntimeConfig()
    {
        var subscriptionId = selectionStore.GetCurrentSubscriptionId();
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return "{}";
        }

        var runtimePath = Path.Combine(runtimeDirectory, subscriptionId, "runtime.yaml");
        return File.Exists(runtimePath) ? File.ReadAllText(runtimePath) : "{}";
    }
}
