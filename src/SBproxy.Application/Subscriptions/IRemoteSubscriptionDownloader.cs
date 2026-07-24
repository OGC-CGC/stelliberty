using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public interface IRemoteSubscriptionDownloader
{
    Task<RemoteSubscriptionDownloadResult> DownloadAsync(RemoteSubscriptionDownloadRequest request, CancellationToken cancellationToken = default);
}
