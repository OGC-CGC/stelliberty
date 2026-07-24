using SBproxy.Domain.Connections;
namespace SBproxy.Application.Connections;

public sealed record ConnectionCloseRequest(ConnectionCloseMode Mode, string? ConnectionId = null);
