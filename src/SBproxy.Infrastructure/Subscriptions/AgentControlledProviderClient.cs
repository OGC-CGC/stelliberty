using System.Text.Json;
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Subscriptions;

public sealed class AgentControlledProviderClient(
    ISubscriptionProviderStateReader reader,
    string agentPipeName) :
    ISubscriptionProviderSyncer,
    ISubscriptionProviderStateReader,
    IDisposable
{
    private readonly JsonRpcPipeClient _agent = new(agentPipeName);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IReadOnlyList<SubscriptionProviderRuntimeState>> ReadStatesAsync(
        CancellationToken cancellationToken = default)
        => reader.ReadStatesAsync(cancellationToken);

    public async Task SyncAsync(
        SubscriptionProvider provider,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_agent.IsConnected)
            {
                await _agent.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            await _agent.RequestAsync(
                    "core.update_provider",
                    JsonSerializer.SerializeToElement(new
                    {
                        provider_type = provider.Type,
                        provider_name = provider.Name,
                    }),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogger.Warning($"Agent Provider update failed: {provider.Name}, {exception.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _agent.Dispose();
        _gate.Dispose();
        (reader as IDisposable)?.Dispose();
    }
}
