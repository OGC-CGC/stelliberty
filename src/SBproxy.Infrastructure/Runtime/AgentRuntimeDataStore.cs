using System.Text.Json;
using SBproxy.Application.Proxies;
using SBproxy.Application.Rules;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Runtime;

public sealed class AgentRuntimeDataStore(
    string pipeName,
    ISubscriptionSelectionStore selectionStore) :
    ISelectedSubscriptionRuntimeStore,
    IProxyConfigSource,
    IProxyNodeShareSource,
    IRuleConfigSource,
    IDisposable
{
    private readonly object _gate = new();
    private readonly JsonRpcPipeClient _client = new(pipeName);
    private bool _isDisposed;

    public SelectedSubscriptionRuntimePaths Save(
        Subscription subscription,
        string originalContent,
        string runtimeConfigContent)
    {
        var result = Request("runtime.save", new
        {
            subscription_id = subscription.Id,
            original = originalContent,
            runtime = runtimeConfigContent,
        });
        return new SelectedSubscriptionRuntimePaths(
            ReadRequiredString(result, "original_path"),
            ReadRequiredString(result, "runtime_path"));
    }

    public string SaveEmpty(string runtimeConfigContent)
    {
        var result = Request("runtime.save_empty", new { runtime = runtimeConfigContent });
        return ReadRequiredString(result, "runtime_path");
    }

    public string ReadRuntimeConfig(string subscriptionId)
    {
        var result = Request("runtime.get", new { subscription_id = subscriptionId });
        return ReadRequiredString(result, "content");
    }

    public string ReadRuntimeConfig()
    {
        var subscriptionId = selectionStore.GetCurrentSubscriptionId();
        return string.IsNullOrWhiteSpace(subscriptionId)
            ? "{}"
            : ReadRuntimeConfig(subscriptionId);
    }

    public string ReadProviderConfig(string path)
    {
        var result = Request("runtime.provider.get", new { path });
        return ReadRequiredString(result, "content");
    }

    public void Delete(string subscriptionId)
        => Request("runtime.delete", new { subscription_id = subscriptionId });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
            _client.Dispose();
        }
    }

    private JsonElement Request(string method, object parameters)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (!_client.IsConnected)
            {
                _client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            return _client.RequestAsync(
                    method,
                    JsonSerializer.SerializeToElement(parameters),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }

    private static string ReadRequiredString(JsonElement result, string name)
        => result.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : throw new InvalidOperationException($"Agent response is missing {name}.");
}
