using SBproxy.Domain.Proxies;
namespace SBproxy.Application.Proxies;

public interface IProxyDelayTester
{
    Task<int> TestDelayAsync(string proxyName, CancellationToken cancellationToken = default);
}
