using SBproxy.Application.Proxies;
using SBproxy.Domain.Proxies;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;

namespace SBproxy.Infrastructure.Proxies;

public sealed class FileRuntimeProxyConfigSource(
    string runtimeDirectory,
    string coreDirectory,
    ISubscriptionSelectionStore selectionStore) : IProxyConfigSource, IProxyNodeShareSource
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

    public string ReadProviderConfig(string path)
    {
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(coreDirectory, path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path);
        return File.ReadAllText(resolvedPath);
    }
}
