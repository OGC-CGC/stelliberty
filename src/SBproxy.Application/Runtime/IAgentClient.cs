using SBproxy.Application.CoreLogs;
using SBproxy.Domain.CoreLogs;

namespace SBproxy.Application.Runtime;

public interface IAgentClient
{
    event EventHandler<CoreSnapshot>? StateChanged;

    event EventHandler<CoreLogMessage>? CoreLogReceived;

    Task<CoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<CoreApplyConfigResult> ApplyConfigAsync(CoreApplyConfigRequest request, CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);
}
