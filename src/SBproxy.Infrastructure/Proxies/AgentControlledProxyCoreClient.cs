using System.Text.Json;
using SBproxy.Application.Connections;
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Proxies;
using SBproxy.Domain.Connections;
using SBproxy.Domain.Proxies;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Proxies;

public sealed class AgentControlledProxyCoreClient(
    IProxyCoreClient reader,
    string agentPipeName) : IProxyCoreClient, IDisposable
{
    private readonly JsonRpcPipeClient _agent = new(agentPipeName);
    private readonly SemaphoreSlim _agentGate = new(1, 1);

    public Task<IReadOnlyList<ConnectionInfo>?> GetConnectionsAsync(
        CancellationToken cancellationToken = default)
        => reader.GetConnectionsAsync(cancellationToken);

    public Task<ProxyRuntimeSnapshot> GetProxiesAsync(
        CancellationToken cancellationToken = default)
        => reader.GetProxiesAsync(cancellationToken);

    public Task<OutboundMode?> GetOutboundModeAsync(
        CancellationToken cancellationToken = default)
        => reader.GetOutboundModeAsync(cancellationToken);

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
        => reader.GetVersionAsync(cancellationToken);

    public Task<CoreRuntimeStats?> GetRuntimeStatsAsync(
        CancellationToken cancellationToken = default)
        => reader.GetRuntimeStatsAsync(cancellationToken);

    public Task<CoreTrafficRate?> GetTrafficAsync(
        CancellationToken cancellationToken = default)
        => reader.GetTrafficAsync(cancellationToken);

    public Task<bool> ChangeProxyAsync(
        ProxyChangeRequest request,
        CancellationToken cancellationToken = default)
        => SendControlAsync("core.change_proxy", new
        {
            group_name = request.GroupName,
            proxy_name = request.ProxyName,
        }, cancellationToken);

    public Task<bool> ClearProxySelectionAsync(
        string groupName,
        CancellationToken cancellationToken = default)
        => SendControlAsync("core.clear_proxy_selection", new
        {
            group_name = groupName,
        }, cancellationToken);

    public Task<bool> CloseConnectionsAsync(
        ConnectionCloseRequest request,
        CancellationToken cancellationToken = default)
        => SendControlAsync("core.close_connections", new
        {
            connection_id = request.Mode == ConnectionCloseMode.Single
                ? request.ConnectionId
                : null,
        }, cancellationToken);

    public Task<bool> SetOutboundModeAsync(
        OutboundMode mode,
        CancellationToken cancellationToken = default)
        => SendControlAsync("core.set_outbound_mode", new
        {
            mode = mode.ToString().ToLowerInvariant(),
        }, cancellationToken);

    public void Dispose()
    {
        _agent.Dispose();
        _agentGate.Dispose();
        (reader as IDisposable)?.Dispose();
    }

    private async Task<bool> SendControlAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await _agentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_agent.IsConnected)
            {
                await _agent.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            await _agent.RequestAsync(
                    method,
                    JsonSerializer.SerializeToElement(parameters),
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogger.Warning($"Agent Core control failed: method={method} error={exception.Message}");
            return false;
        }
        finally
        {
            _agentGate.Release();
        }
    }
}
