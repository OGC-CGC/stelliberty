using System.Text.Json;
using SBproxy.Application.Platform;

namespace SBproxy.Infrastructure.Core;

public sealed class AgentSystemProxyService(string pipeName) : ISystemProxyService
{
    public bool IsEnabled()
    {
        try
        {
            using var client = new JsonRpcPipeClient(pipeName);
            client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var empty = JsonDocument.Parse("{}");
            var status = client.RequestAsync(
                    "agent.status",
                    empty.RootElement,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return status.TryGetProperty("system_proxy_enabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public SystemProxyOperationResult Enable(SystemProxyApplicationRequest request)
    {
        return SetEnabled(true, request);
    }

    public SystemProxyOperationResult Disable()
    {
        return SetEnabled(false, null);
    }

    private SystemProxyOperationResult SetEnabled(
        bool enabled,
        SystemProxyApplicationRequest? request)
    {
        try
        {
            using var client = new JsonRpcPipeClient(pipeName);
            client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            var parameters = JsonSerializer.SerializeToElement(new
            {
                enabled,
                host = request?.Host,
                port = request?.Port,
                bypass_rules = request?.BypassRules,
                is_pac_mode_enabled = request?.IsPacModeEnabled,
                pac_script = request?.PacScript,
            });
            client.RequestAsync(
                    "agent.set_system_proxy",
                    parameters,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new SystemProxyOperationResult(
                true,
                enabled ? "System proxy enabled" : "System proxy disabled");
        }
        catch (Exception exception)
        {
            return new SystemProxyOperationResult(false, exception.Message);
        }
    }
}
