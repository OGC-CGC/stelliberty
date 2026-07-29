using System.Text.Json;
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Runtime;

namespace SBproxy.Infrastructure.Core;

public sealed class AgentCoreUpdater(string agentPipeName) : ICoreUpdater, IDisposable
{
    private readonly JsonRpcPipeClient _client = new(agentPipeName);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CoreUpdateResult> UpdateAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            using var empty = JsonDocument.Parse("{}");
            var result = await _client.RequestAsync(
                    "core.update",
                    empty.RootElement,
                    cancellationToken)
                .ConfigureAwait(false);
            var statusText = result.GetProperty("status").GetString();
            var status = Enum.TryParse<CoreUpdateStatus>(statusText, out var parsed)
                ? parsed
                : CoreUpdateStatus.Failed;
            return new CoreUpdateResult(
                status,
                result.TryGetProperty("version", out var version)
                    ? version.GetString()
                    : null,
                result.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "Core update failed"
                    : "Core update failed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = $"Core update failed: {exception.Message}";
            AppLogger.Warning(message);
            return new CoreUpdateResult(CoreUpdateStatus.Failed, null, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }
}
