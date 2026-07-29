using System.Text.Json;
using SBproxy.Application.Platform;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Platform;

public sealed class AgentServiceModeManager(string agentPipeName) :
    IServiceModeManager,
    IDisposable
{
    private readonly JsonRpcPipeClient _client = new(agentPipeName);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ServiceModeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RequestAsync(
                "service.status",
                new { },
                cancellationToken).ConfigureAwait(false);
            var state = Enum.TryParse<ServiceModeState>(
                result.GetProperty("state").GetString(),
                out var parsed)
                    ? parsed
                    : ServiceModeState.Unknown;
            return new ServiceModeStatus(
                state,
                result.GetProperty("message").GetString() ?? string.Empty,
                Seconds(result, "uptime_seconds"),
                Seconds(result, "heartbeat_seconds"),
                String(result, "core_state"),
                Int32(result, "core_pid"),
                String(result, "core_last_error"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ServiceModeStatus.Unavailable(exception.Message);
        }
    }

    public Task<ServiceModeOperationResult> InstallOrUpdateAsync(
        CancellationToken cancellationToken = default)
        => OperationAsync("service.install", new { }, cancellationToken);

    public Task<ServiceModeOperationResult> UninstallAsync(
        CancellationToken cancellationToken = default)
        => OperationAsync("service.uninstall", new { }, cancellationToken);

    public Task<ServiceModeOperationResult> StartCoreHostAsync(
        ServiceModeCoreHostRequest request,
        CancellationToken cancellationToken = default)
        => OperationAsync(
            "service.start_core",
            new
            {
                mihomo_path = request.MihomoPath,
                data_core_dir = request.DataCoreDir,
                config_path = request.ConfigPath,
            },
            cancellationToken);

    public Task<ServiceModeOperationResult> StopCoreHostAsync(
        CancellationToken cancellationToken = default)
        => OperationAsync("service.stop_core", new { }, cancellationToken);

    public Task<ServiceModeOperationResult> RestartCoreHostAsync(
        CancellationToken cancellationToken = default)
        => OperationAsync("service.restart_core", new { }, cancellationToken);

    public Task<ServiceModeOperationResult> SendHeartbeatAsync(
        CancellationToken cancellationToken = default)
        => OperationAsync("service.heartbeat", new { }, cancellationToken);

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    private async Task<ServiceModeOperationResult> OperationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RequestAsync(method, parameters, cancellationToken)
                .ConfigureAwait(false);
            var type = Enum.TryParse<ServiceModeOperationType>(
                result.GetProperty("type").GetString(),
                out var parsed)
                    ? parsed
                    : ServiceModeOperationType.Failed;
            return new ServiceModeOperationResult(
                type,
                result.GetProperty("message").GetString() ?? string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ServiceModeOperationResult.Failed(exception.Message);
        }
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            return await _client.RequestAsync(
                    method,
                    JsonSerializer.SerializeToElement(parameters),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static int? Int32(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private static TimeSpan? Seconds(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.TryGetInt64(out var seconds)
            && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
}
