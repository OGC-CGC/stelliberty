using System.Text.Json;
using SBproxy.Application.Proxies;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Subscriptions;

public sealed class AgentSubscriptionDataStore :
    ISubscriptionStore,
    ISubscriptionSelectionStore,
    IProxySelectionStore,
    IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly JsonRpcPipeClient _client;
    private bool _isDisposed;

    public AgentSubscriptionDataStore(string pipeName)
    {
        _client = new JsonRpcPipeClient(pipeName);
    }

    public void Save(Subscription subscription, string originalContent)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        Request("subscriptions.save", new
        {
            subscription,
            content = originalContent,
        });
    }

    public void UpdateSubscription(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        Request("subscriptions.update", new { subscription });
    }

    public void SaveSubscriptions(IReadOnlyList<Subscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        Request("subscriptions.reorder", new { subscriptions });
    }

    public void SaveContent(string subscriptionId, string originalContent)
    {
        Request("subscriptions.content.set", new
        {
            subscription_id = subscriptionId,
            content = originalContent,
        });
    }

    public IReadOnlyList<Subscription> LoadSubscriptions()
    {
        var result = Request("subscriptions.list", new object());
        if (!result.TryGetProperty("subscriptions", out var subscriptions)
            || subscriptions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return subscriptions.Deserialize<List<Subscription>>(SerializerOptions) ?? [];
    }

    public string ReadContent(string subscriptionId)
    {
        var result = Request("subscriptions.content.get", new
        {
            subscription_id = subscriptionId,
        });
        return result.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
    }

    public string GetContentPath(string subscriptionId)
    {
        var result = Request("subscriptions.content.path", new
        {
            subscription_id = subscriptionId,
        });
        return result.TryGetProperty("path", out var path)
            ? path.GetString() ?? string.Empty
            : string.Empty;
    }

    public void Delete(string subscriptionId)
    {
        Request("subscriptions.delete", new
        {
            subscription_id = subscriptionId,
        });
    }

    public string? GetCurrentSubscriptionId()
    {
        var result = Request("subscriptions.selection.get", new object());
        return result.TryGetProperty("subscription_id", out var subscriptionId)
            && subscriptionId.ValueKind == JsonValueKind.String
                ? subscriptionId.GetString()
                : null;
    }

    public void SetCurrentSubscriptionId(string? subscriptionId)
    {
        Request("subscriptions.selection.set", new
        {
            subscription_id = subscriptionId,
        });
    }

    public IReadOnlyDictionary<string, string> GetSelections(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        var result = Request("proxies.selections.get", new
        {
            subscription_id = subscriptionId,
        });
        if (!result.TryGetProperty("selections", out var selections)
            || selections.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        var values = selections.Deserialize<Dictionary<string, string>>(SerializerOptions);
        return values is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(values, StringComparer.Ordinal);
    }

    public void SetSelection(string subscriptionId, string groupName, string proxyName)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId)
            || string.IsNullOrWhiteSpace(groupName)
            || string.IsNullOrWhiteSpace(proxyName))
        {
            return;
        }
        Request("proxies.selections.set", new
        {
            subscription_id = subscriptionId,
            group_name = groupName,
            proxy_name = proxyName,
        });
    }

    public void RemoveSelection(string subscriptionId, string groupName)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(groupName))
        {
            return;
        }
        Request("proxies.selections.remove", new
        {
            subscription_id = subscriptionId,
            group_name = groupName,
        });
    }

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
}
