using SBproxy.Domain.Subscriptions;
using SBproxy.Application.Runtime;

namespace SBproxy.Application.Subscriptions;

public sealed record SelectedSubscriptionRuntimeRequest(
    IReadOnlyList<RuntimeOverride> Overrides,
    RuntimeConfigParams RuntimeParams);
