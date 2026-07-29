using System.Text.Json;
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Updates;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Updates;

public sealed class AgentAppUpdateChecker(
    string agentPipeName,
    Func<string> channelProvider) : IAppUpdateChecker, IDisposable
{
    private readonly JsonRpcPipeClient _client = new(agentPipeName);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppUpdateCheckResult CheckForUpdates()
    {
        try
        {
            return CheckAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"App update check failed: {exception.Message}");
            return new AppUpdateCheckResult(
                false,
                null,
                exception.Message,
                IsFailure: true);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    private async Task<AppUpdateCheckResult> CheckAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var result = await _client.RequestAsync(
                    "app.update.check",
                    JsonSerializer.SerializeToElement(new
                    {
                        channel = channelProvider(),
                    }),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new AppUpdateCheckResult(
                result.GetProperty("has_update").GetBoolean(),
                result.TryGetProperty("latest_version", out var latest)
                    ? latest.GetString()
                    : null,
                result.GetProperty("message").GetString() ?? string.Empty,
                result.TryGetProperty("release_url", out var url)
                    ? url.GetString()
                    : null,
                result.TryGetProperty("is_failure", out var failure)
                    && failure.GetBoolean());
        }
        finally
        {
            _gate.Release();
        }
    }
}
