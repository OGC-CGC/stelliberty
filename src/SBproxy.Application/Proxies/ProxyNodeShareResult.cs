namespace SBproxy.Application.Proxies;

public sealed record ProxyNodeShareResult(
    string Name,
    string Type,
    string Server,
    string Port,
    string ProviderName,
    string Content,
    bool IsYaml);
