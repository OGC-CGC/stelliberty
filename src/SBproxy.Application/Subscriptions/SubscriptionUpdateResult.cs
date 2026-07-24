using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public sealed record SubscriptionUpdateResult(
    IReadOnlyList<string> UpdatedSubscriptionIds,
    IReadOnlyList<string> SkippedSubscriptionIds);
