using System.Runtime.CompilerServices;
using System.Text.Json;
using SBproxy.Application.Settings;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Settings;

public sealed class AgentAppSettingsStore : IAppSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly JsonRpcPipeClient _client;
    private readonly ConditionalWeakTable<AppSettings, Baseline> _baselines = new();
    private bool _isDisposed;

    public AgentAppSettingsStore(string pipeName)
    {
        _client = new JsonRpcPipeClient(pipeName);
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var snapshot = GetSnapshot();
            var settings = snapshot.Settings.Deserialize<AppSettings>(SerializerOptions)
                ?? new AppSettings();
            Track(settings, snapshot.Revision, SerializeProperties(settings));
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ThrowIfDisposed();
            var current = SerializeProperties(settings);
            var baseline = _baselines.TryGetValue(settings, out var tracked)
                ? tracked
                : new Baseline(0, new Dictionary<string, JsonElement>(StringComparer.Ordinal));
            var changes = current
                .Where(item => !baseline.Properties.TryGetValue(item.Key, out var previous)
                    || !JsonElement.DeepEquals(previous, item.Value))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            if (changes.Count == 0)
            {
                return;
            }

            Snapshot committed;
            try
            {
                committed = Patch(changes, baseline.Revision);
            }
            catch (IpcRemoteException exception) when (exception.Code == "settings.revision_conflict")
            {
                var latest = GetSnapshot();
                committed = Patch(changes, latest.Revision);
            }

            var committedSettings = committed.Settings.Deserialize<AppSettings>(SerializerOptions)
                ?? settings;
            Track(settings, committed.Revision, SerializeProperties(committedSettings));
        }
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

    private Snapshot GetSnapshot()
    {
        EnsureConnected();
        using var empty = JsonDocument.Parse("{}");
        var result = _client.RequestAsync(
                "settings.get",
                empty.RootElement,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return ParseSnapshot(result);
    }

    private Snapshot Patch(IReadOnlyDictionary<string, JsonElement> changes, ulong revision)
    {
        EnsureConnected();
        var parameters = JsonSerializer.SerializeToElement(new
        {
            expected_revision = revision,
            changes,
        });
        var result = _client.RequestAsync(
                "settings.patch",
                parameters,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return ParseSnapshot(result);
    }

    private void EnsureConnected()
    {
        if (_client.IsConnected)
        {
            return;
        }
        _client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private void Track(
        AppSettings settings,
        ulong revision,
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        _baselines.Remove(settings);
        _baselines.Add(settings, new Baseline(revision, properties));
    }

    private static Snapshot ParseSnapshot(JsonElement result)
    {
        var revision = result.TryGetProperty("revision", out var revisionProperty)
            ? revisionProperty.GetUInt64()
            : 0;
        if (!result.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Agent settings response is missing settings.");
        }
        return new Snapshot(revision, settings.Clone());
    }

    private static IReadOnlyDictionary<string, JsonElement> SerializeProperties(AppSettings settings)
    {
        var document = JsonSerializer.SerializeToElement(settings, SerializerOptions);
        return document
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private sealed record Baseline(
        ulong Revision,
        IReadOnlyDictionary<string, JsonElement> Properties);

    private sealed record Snapshot(ulong Revision, JsonElement Settings);
}
