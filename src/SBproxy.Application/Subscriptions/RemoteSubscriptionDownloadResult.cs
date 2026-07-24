using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public sealed record RemoteSubscriptionDownloadResult(string Content, SubscriptionTrafficInfo? TrafficInfo = null);
