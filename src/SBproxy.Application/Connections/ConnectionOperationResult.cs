using SBproxy.Domain.Connections;
namespace SBproxy.Application.Connections;

public sealed record ConnectionOperationResult(
    ConnectionListState State,
    ConnectionCloseRequest Request,
    IReadOnlyList<string> ClosedConnectionIds,
    bool HasClosedAllConnections);
