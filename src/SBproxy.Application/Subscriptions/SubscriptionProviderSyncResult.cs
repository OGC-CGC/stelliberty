using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public sealed record SubscriptionProviderSyncResult(
    IReadOnlyList<string> SyncedProviderNames,
    IReadOnlyList<string> SkippedProviderNames)
{
    public IReadOnlyList<string> FailedProviderNames { get; init; } = [];
}
