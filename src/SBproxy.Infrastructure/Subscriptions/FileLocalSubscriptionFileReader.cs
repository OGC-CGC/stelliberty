using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;

namespace SBproxy.Infrastructure.Subscriptions;

public sealed class FileLocalSubscriptionFileReader : ILocalSubscriptionFileReader
{
    public string ReadAllText(string filePath)
    {
        return File.ReadAllText(filePath);
    }
}
