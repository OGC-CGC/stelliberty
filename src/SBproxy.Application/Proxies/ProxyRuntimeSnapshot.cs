using SBproxy.Domain.Proxies;
namespace SBproxy.Application.Proxies;

public sealed record ProxyRuntimeSnapshot(IReadOnlyList<ProxyRuntimeEntry> Entries);

public sealed record ProxyRuntimeEntry(
    string Name,
    string Type,
    string? Now,
    string? Fixed,
    IReadOnlyList<string> All,
    bool IsHidden,
    string? ProviderName = null);
