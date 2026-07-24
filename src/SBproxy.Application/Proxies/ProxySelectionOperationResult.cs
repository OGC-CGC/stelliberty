using SBproxy.Domain.Proxies;
using SBproxy.Application.Connections;
using SBproxy.Domain.Connections;

namespace SBproxy.Application.Proxies;

public sealed record ProxySelectionOperationResult(
    ProxySelectionResult Selection,
    ConnectionCloseRequest ConnectionCloseRequest,
    IReadOnlyList<string> ClosedConnectionIds,
    bool HasClosedAllConnections);
