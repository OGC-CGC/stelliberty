namespace SBproxy.Application.Proxies;

public interface IProxyNodeShareSource
{
    string ReadRuntimeConfig();

    string ReadProviderConfig(string path);
}
