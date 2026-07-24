using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public sealed record RemoteSubscriptionDownloadRequest(
    string SubscriptionId,
    string SourceLocation,
    string UserAgent,
    SubscriptionUpdateProxyMode ProxyMode,
    string AgeSecretKey = "");
