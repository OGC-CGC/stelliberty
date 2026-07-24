using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public interface ISubscriptionFileOpener
{
    void OpenSubscriptionFile(string subscriptionId);
}
