using System.Text.Json;
using SBproxy.Application.Proxies;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Proxies;

public sealed class AgentProxyDelayTester(
    string agentPipeName,
    Func<string> testUrlFactory,
    int timeoutMilliseconds) : IProviderProxyDelayTester, IDisposable
{
    private readonly JsonRpcPipeClient _client = new(agentPipeName);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<int> TestDelayAsync(
        string proxyName,
        CancellationToken cancellationToken = default)
        => TestAsync(proxyName, null, cancellationToken);

    public Task<int> TestProviderDelayAsync(
        string providerName,
        string proxyName,
        CancellationToken cancellationToken = default)
        => TestAsync(proxyName, providerName, cancellationToken);

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    private async Task<int> TestAsync(
        string proxyName,
        string? providerName,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            var result = await _client.RequestAsync(
                    "core.test_delay",
                    JsonSerializer.SerializeToElement(new
                    {
                        proxy_name = proxyName,
                        provider_name = providerName,
                        url = testUrlFactory(),
                        timeout = timeoutMilliseconds,
                    }),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.TryGetProperty("delay", out var delay)
                && delay.TryGetInt32(out var value)
                && value > 0
                    ? value
                    : -1;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return -1;
        }
        finally
        {
            _gate.Release();
        }
    }
}
