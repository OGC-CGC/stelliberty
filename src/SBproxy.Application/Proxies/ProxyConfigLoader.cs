using SBproxy.Domain.Proxies;
namespace SBproxy.Application.Proxies;

public sealed class ProxyConfigLoader(
    IProxyConfigSource source,
    ProxyConfigParser parser,
    Func<bool> isCoreRunning)
{
    public ProxyConfig LoadConfig()
    {
        if (!isCoreRunning())
        {
            return new ProxyConfig([], new Dictionary<string, ProxyNode>());
        }

        return parser.Parse(source.ReadRuntimeConfig());
    }
}
