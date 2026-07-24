using SBproxy.Application.Rules;
using SBproxy.Domain.Rules;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;

namespace SBproxy.Infrastructure.Rules;

public sealed class FileRuntimeRuleConfigSource(string runtimeDirectory, ISubscriptionSelectionStore selectionStore) : IRuleConfigSource
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
