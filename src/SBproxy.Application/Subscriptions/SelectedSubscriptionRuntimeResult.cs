using SBproxy.Domain.Subscriptions;
using SBproxy.Application.Runtime;

namespace SBproxy.Application.Subscriptions;

public sealed record SelectedSubscriptionRuntimeResult(
    Subscription Subscription,
    string RuntimeConfigContent,
    string? OriginalContentPath = null,
    string? RuntimeConfigPath = null);
