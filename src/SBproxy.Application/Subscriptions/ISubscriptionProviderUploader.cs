using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public interface ISubscriptionProviderUploader
{
    Task<SubscriptionProviderUploadResult> UploadAsync(SubscriptionProvider provider, string sourcePath, CancellationToken cancellationToken = default);
}
