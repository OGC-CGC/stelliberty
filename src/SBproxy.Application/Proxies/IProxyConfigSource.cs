using SBproxy.Domain.Proxies;
namespace SBproxy.Application.Proxies;

public interface IProxyConfigSource
{
    string ReadRuntimeConfig();
}
