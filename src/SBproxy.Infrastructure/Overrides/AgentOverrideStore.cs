using System.Text.Json;
using SBproxy.Application.Overrides;
using SBproxy.Domain.Overrides;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Overrides;

public sealed class AgentOverrideStore(string pipeName) : IOverrideStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly JsonRpcPipeClient _client = new(pipeName);
    private bool _isDisposed;

    public void Save(OverrideProfile overrideProfile, string content)
        => Request("overrides.save", new { profile = overrideProfile, content });

    public IReadOnlyList<OverrideProfile> LoadOverrides()
    {
        var result = Request("overrides.list", new object());
        return result.TryGetProperty("overrides", out var profiles)
            && profiles.ValueKind == JsonValueKind.Array
                ? profiles.Deserialize<List<OverrideProfile>>(SerializerOptions) ?? []
                : [];
    }

    public string ReadContent(string overrideId)
    {
        var result = Request("overrides.content.get", new { override_id = overrideId });
        return result.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
    }

    public string GetContentPath(string overrideId)
    {
        var result = Request("overrides.content.path", new { override_id = overrideId });
        return result.TryGetProperty("path", out var path)
            ? path.GetString() ?? string.Empty
            : string.Empty;
    }

    public void SaveOverrides(IReadOnlyList<OverrideProfile> overrides)
        => Request("overrides.reorder", new { overrides });

    public void Delete(string overrideId)
        => Request("overrides.delete", new { override_id = overrideId });

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
