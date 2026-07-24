using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public interface ILocalSubscriptionFileReader
{
    string ReadAllText(string filePath);
}
