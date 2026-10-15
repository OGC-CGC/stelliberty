using Stelliberty.Application.Runtime;
using Stelliberty.Application.Proxies;
using Stelliberty.Application.Subscriptions;
using Stelliberty.Infrastructure.Tray;

namespace Stelliberty.Desktop.Services;

internal sealed class DesktopTraySession : IDisposable, IBackgroundOperationClient, IProxyDelayResultSink, ISubscriptionProviderSource
{
    private readonly TrayIpcClient _client = new();
    private string? _sessionId;
    private int _isDisconnected;

    public event EventHandler? ActivationRequested;

    public event EventHandler? ToggleRequested;

    public event EventHandler? Disconnected;
    public event EventHandler<BackgroundTaskStatus>? BackgroundChanged;

    public bool CanExitToBackground { get; private set; }

    public bool IsDisconnected => Volatile.Read(ref _isDisconnected) != 0;

    public async Task RegisterAsync(string sessionToken, CancellationToken cancellationToken)
    {
        _client.ActivationRequested += OnActivationRequested;
        _client.ToggleRequested += OnToggleRequested;
        _client.Disconnected += OnDisconnected;
        _client.BackgroundChanged += OnBackgroundChanged;
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var hello = await _client.HelloAsync(Environment.ProcessId, cancellationToken).ConfigureAwait(false);
        CanExitToBackground = hello.Capabilities.Contains("background_tray", StringComparer.Ordinal);
        var result = await _client.RegisterUiAsync(
            sessionToken,
            Environment.ProcessId,
            cancellationToken).ConfigureAwait(false);
        _sessionId = result.SessionId;
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is null)
        {
            return;
        }

        await _client.UnregisterUiAsync(_sessionId, cancellationToken).ConfigureAwait(false);
        _sessionId = null;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(BackgroundOperationRequest request, CancellationToken cancellationToken = default)
    {
        // 取得授权后必须接收其 ID 才能释放，避免调用方取消时遗留后台占用。
        cancellationToken.ThrowIfCancellationRequested();
        BackgroundOperationGrant grant;
        try { grant = await _client.AcquireOperationAsync(request, CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // 响应丢失时授权结果未知，关闭连接，让后台回收可能已经授予的占用。
            _client.Dispose();
            OnDisconnected(this, EventArgs.Empty);
            throw;
        }
        if (grant.Id is not { } id) return null;
        var lease = new OperationLease(this, id);
        if (cancellationToken.IsCancellationRequested)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return lease;
    }

    private sealed class OperationLease(DesktopTraySession session, Guid id) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await session._client.ReleaseOperationAsync(id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
            {
                session._client.Dispose();
                session.OnDisconnected(this, EventArgs.Empty);
            }
        }
    }

    public Task ShutdownTrayAsync(CancellationToken cancellationToken) =>
        _client.ShutdownAsync(cancellationToken);

    public Task<BackgroundTaskStatus> GetBackgroundStatusAsync(CancellationToken cancellationToken) =>
        _client.GetBackgroundStatusAsync(cancellationToken);

    public Task<string> CaptureScopeAsync(CancellationToken cancellationToken = default) =>
        _client.CaptureProxyDelayScopeAsync(cancellationToken);

    public Task PublishAsync(ProxyDelayPublication publication, CancellationToken cancellationToken = default) =>
        _client.PublishProxyDelayAsync(publication, cancellationToken);

    public Task<SubscriptionProviderSnapshot> ReadAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
        _client.GetSubscriptionProvidersAsync(subscriptionId, cancellationToken);

    public Task SyncAsync(string subscriptionId, string providerType, string providerName, CancellationToken cancellationToken = default) =>
        _client.SyncSubscriptionProviderAsync(subscriptionId, providerType, providerName, cancellationToken);

    private void OnBackgroundChanged(object? sender, BackgroundTaskStatus status) =>
        BackgroundChanged?.Invoke(this, status);

    private void OnActivationRequested(object? sender, EventArgs args) =>
        ActivationRequested?.Invoke(this, EventArgs.Empty);

    private void OnToggleRequested(object? sender, EventArgs args) =>
        ToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnDisconnected(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref _isDisconnected, 1);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _client.ActivationRequested -= OnActivationRequested;
        _client.ToggleRequested -= OnToggleRequested;
        _client.Disconnected -= OnDisconnected;
        _client.BackgroundChanged -= OnBackgroundChanged;
        _client.Dispose();
        _sessionId = null;
        ActivationRequested = null;
        ToggleRequested = null;
        Disconnected = null;
    }
}
