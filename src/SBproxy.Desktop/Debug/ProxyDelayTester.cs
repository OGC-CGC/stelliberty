#if DEBUG
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Proxies;
using SBproxy.Domain.Proxies;

namespace SBproxy.Desktop.Debug;

internal sealed class ProxyDelayTester : IProxyDelayTester
{
    public Task<int> TestDelayAsync(string proxyName, CancellationToken cancellationToken = default)
    {
        AppLogger.Info($"Debug proxy delay test used local result: {proxyName}");
        return Task.FromResult(50);
    }
}
#endif
