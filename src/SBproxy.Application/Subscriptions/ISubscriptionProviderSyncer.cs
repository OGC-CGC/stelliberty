using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public interface ISubscriptionProviderSyncer
{
    Task SyncAsync(SubscriptionProvider provider, CancellationToken cancellationToken = default);
}
