using Stelliberty.Application.Updates;

namespace Stelliberty.Application.Runtime;

public sealed record BackgroundTaskStatus(
    long Revision,
    long SubscriptionRevision,
    AppUpdateAutoCheckResult? AppUpdate,
    string? DelaySubscriptionId,
    IReadOnlyDictionary<string, int> Delays,
    long ProviderRevision = 0,
    long SettingsRevision = 0,
    long ClientSubscriptionRevision = 0,
    long ProxyRevision = 0,
    IReadOnlyList<BackgroundOperationState>? Operations = null);
