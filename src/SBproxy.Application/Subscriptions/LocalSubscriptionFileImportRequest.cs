using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public sealed record LocalSubscriptionFileImportRequest(
    string Name,
    string FilePath,
    int AutoTestDelayIntervalMinutes = 0);
