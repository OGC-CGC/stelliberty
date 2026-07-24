using SBproxy.Application.Proxies;
using SBproxy.Domain.Proxies;

namespace SBproxy.Infrastructure.Proxies;

public sealed class FileRuntimeProxyConfigProvider(ProxyConfigLoader loader) : IProxyConfigProvider
{
    public Task<ProxyConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(loader.LoadConfig, cancellationToken);
    }
}
