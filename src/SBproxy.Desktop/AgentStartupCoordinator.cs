using SBproxy.Application.Diagnostics;
using SBproxy.Application.Platform;
using SBproxy.Desktop.Services;
using SBproxy.Infrastructure.Core;
using SBproxy.Native.Hub;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SBproxy.Desktop;

// UI 只连接并控制已经运行的 User Agent。
internal static class AgentStartupCoordinator
{
#if DEBUG

    public static readonly string PipeName = BuildAgentEndpoint(AppMetadata.PipePrefix + "_agent_dev");
    public static readonly string MihomoPipe = BuildMihomoEndpoint(AppMetadata.PipePrefix + "_mihomo_dev");
#else
    public static readonly string PipeName = BuildAgentEndpoint(AppMetadata.PipePrefix + "_agent_prod");
    public static readonly string MihomoPipe = BuildMihomoEndpoint(AppMetadata.PipePrefix + "_mihomo_prod");
#endif

    private const int ProtocolVersion = 2;
    private static readonly SemaphoreSlim UiSessionGate = new(1, 1);
    private static JsonRpcPipeClient? _uiSession;
    private static Action? _activateUi;
    private static Action? _shutdownUi;
    private static Action<string>? _updateAvailable;

    public static async Task<BootstrapResult> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connected = await TryConnectAndStartCoreAsync(cancellationToken).ConfigureAwait(false);
            return connected ?? BootstrapResult.Failure("User Agent is not running.");
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"User Agent connection failed: {exception.Message}");
            return BootstrapResult.Failure(exception.Message);
        }
    }

    public static async Task<BootstrapResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        return await SendCoreCommandAsync("core.stop", cancellationToken).ConfigureAwait(false);
    }

    public static Task<BootstrapResult> ResumeCoreAsync(CancellationToken cancellationToken)
    {
        return EnsureStartedAsync(cancellationToken);
    }

    public static async Task<ServiceModeOperationResult> SetTunHostAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new JsonRpcPipeClient(PipeName);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var parameters = JsonSerializer.SerializeToElement(new { enabled });
            await client.RequestAsync("agent.set_tun", parameters, cancellationToken)
                .ConfigureAwait(false);
            return ServiceModeOperationResult.Success(
                enabled ? "Service mode is active." : "Normal mode is active.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ServiceModeOperationResult.Canceled("TUN host switch was canceled.");
        }
        catch (Exception exception)
        {
            return ServiceModeOperationResult.Failed(exception.Message);
        }
    }

    public static async Task<BootstrapResult> ActivateUiAsync(CancellationToken cancellationToken = default)
    {
        var started = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Ok)
        {
            return started;
        }

        return await SendCoreCommandAsync("ui.activate", cancellationToken).ConfigureAwait(false);
    }

    public static async Task RegisterUiAsync(
        Action activateUi,
        Action shutdownUi,
        Action<string> updateAvailable,
        CancellationToken cancellationToken = default)
    {
        await UiSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_uiSession is not null)
            {
                return;
            }

            var client = new JsonRpcPipeClient(PipeName);
            client.EventReceived += OnUiEvent;
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _activateUi = activateUi;
                _shutdownUi = shutdownUi;
                _updateAvailable = updateAvailable;
                var parameters = JsonSerializer.SerializeToElement(new { pid = Environment.ProcessId });
                var registration = await client.RequestAsync(
                    "ui.register",
                    parameters,
                    cancellationToken).ConfigureAwait(false);
                _uiSession = client;
                if (registration.TryGetProperty("activate", out var activateProperty)
                    && activateProperty.ValueKind == JsonValueKind.True)
                {
                    activateUi();
                }
                if (registration.TryGetProperty("update_version", out var updateVersion)
                    && updateVersion.GetString() is { Length: > 0 } version)
                {
                    updateAvailable(version);
                }
            }
            catch
            {
                client.EventReceived -= OnUiEvent;
                await client.DisposeAsync().ConfigureAwait(false);
                _activateUi = null;
                _shutdownUi = null;
                _updateAvailable = null;
                throw;
            }
        }
        finally
        {
            UiSessionGate.Release();
        }
    }

    public static async Task UnregisterUiAsync(CancellationToken cancellationToken = default)
    {
        await UiSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_uiSession is null)
            {
                return;
            }

            var client = _uiSession;
            _uiSession = null;
            try
            {
                var parameters = JsonSerializer.SerializeToElement(new { pid = Environment.ProcessId });
                await client.RequestAsync("ui.unregister", parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLogger.Warning($"User Agent UI unregister failed: {exception.Message}");
            }
            finally
            {
                client.EventReceived -= OnUiEvent;
                await client.DisposeAsync().ConfigureAwait(false);
                _activateUi = null;
                _shutdownUi = null;
                _updateAvailable = null;
            }
        }
        finally
        {
            UiSessionGate.Release();
        }
    }

    public static Task<BootstrapResult> ExitApplicationAsync(CancellationToken cancellationToken = default)
    {
        return SendCoreCommandAsync("agent.exit_application", cancellationToken);
    }

    private static async Task<BootstrapResult?> TryConnectAndStartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new JsonRpcPipeClient(PipeName);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));
            await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            using var empty = JsonDocument.Parse("{}");
            var hello = await client.RequestAsync(
                "agent.hello",
                empty.RootElement,
                cancellationToken).ConfigureAwait(false);
            var version = hello.TryGetProperty("protocol_version", out var versionProperty)
                ? versionProperty.GetInt32()
                : 0;
            if (version != ProtocolVersion)
            {
                return BootstrapResult.Failure(
                    $"Unsupported User Agent protocol version: {version}.");
            }

            await client.RequestAsync(
                "core.start",
                empty.RootElement,
                cancellationToken).ConfigureAwait(false);
            return BootstrapResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IpcRemoteException exception)
        {
            return BootstrapResult.Failure(
                $"User Agent rejected the core startup request: {exception.Message}");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<BootstrapResult> SendCoreCommandAsync(
        string method,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new JsonRpcPipeClient(PipeName);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using var empty = JsonDocument.Parse("{}");
            await client.RequestAsync(method, empty.RootElement, cancellationToken).ConfigureAwait(false);
            return BootstrapResult.Success();
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"User Agent command failed: method={method} error={exception.Message}");
            return BootstrapResult.Failure(exception.Message);
        }
    }

    private static void OnUiEvent(object? sender, EventNotification notification)
    {
        switch (notification.Event)
        {
            case "ui.activate_requested":
                _activateUi?.Invoke();
                break;
            case "ui.shutdown_requested":
                _shutdownUi?.Invoke();
                break;
            case "app.update_available":
                if (notification.Data.TryGetProperty("version", out var version)
                    && version.GetString() is { Length: > 0 } value)
                {
                    _updateAvailable?.Invoke(value);
                }
                break;
        }
    }

    private static string BuildMihomoEndpoint(string name)
    {
        return OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{name}"
            : Path.Combine(PrivateIpcDirectory(), $"{name}.sock");
    }

    private static string BuildAgentEndpoint(string name)
    {
        return OperatingSystem.IsWindows()
            ? name
            : Path.Combine(PrivateIpcDirectory(), $"{name}.sock");
    }

    private static string PrivateIpcDirectory()
        => UnixIpcDirectory(GetEuid());

    internal static string UnixIpcDirectory(uint effectiveUserId)
        => $"/tmp/{AppMetadata.PipePrefix}-ipc-{effectiveUserId}";

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
}
