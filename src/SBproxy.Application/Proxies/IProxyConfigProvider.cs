using SBproxy.Domain.Proxies;
namespace SBproxy.Application.Proxies;

public interface IProxyConfigProvider
{
    Task<ProxyConfig> LoadAsync(CancellationToken cancellationToken = default);
}
