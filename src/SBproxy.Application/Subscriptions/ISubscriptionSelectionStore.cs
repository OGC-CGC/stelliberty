using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public interface ISubscriptionSelectionStore
{
    string? GetCurrentSubscriptionId();

    void SetCurrentSubscriptionId(string? subscriptionId);
}
