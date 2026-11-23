using System.Text.Json;
using Stelliberty.Application.Diagnostics;
using Stelliberty.Application.Runtime;
using Stelliberty.Application.Settings;
using Stelliberty.Infrastructure.Platform;
using Stelliberty.Infrastructure.Settings;
using YamlDotNet.RepresentationModel;

namespace Stelliberty.Tray;

internal sealed partial class TrayCoreRuntimeHost
{
    private CoreApplyConfigRequest? _outboundBaseConfig;
    private CoreApplyConfigRequest? _pendingOutboundConfig;
    private string? _appliedOutboundInterface;
    private CoreApplyConfigRequest? _normalOutboundBaseConfig;
    private bool _normalCoreHasManagedInterface;
    private volatile bool _isOutboundInterfaceUnavailable;

    public async Task<bool> RefreshOutboundInterfaceAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (!_isCoreRequested || _isShutdownSuspended)
                {
                    return false;
                }
            }

            if (CurrentStatus.Snapshot.State != CoreState.Running)
            {
                return _isOutboundInterfaceUnavailable && (await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false)).IsSuccess;
            }

            await UpdateRunningOutboundInterfaceAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OutboundInterfaceUnavailableException)
        {
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task UpdateRunningOutboundInterfaceAsync(CancellationToken cancellationToken)
    {
        var settings = LoadOutboundSettings();
        var request = _pendingOutboundConfig ?? _outboundBaseConfig;
        if (request is null || (settings.OutboundInterfaceMode == OutboundInterfaceMode.FollowConfiguration
            && _appliedOutboundInterface is null && !_isOutboundInterfaceUnavailable))
        {
            return;
        }

        var outboundInterface = await ResolveOutboundInterfaceAsync(settings, coreIsRunning: true, cancellationToken).ConfigureAwait(false);
        if (_pendingOutboundConfig is not null || !string.Equals(outboundInterface, _appliedOutboundInterface, StringComparison.Ordinal))
        {
            var currentMode = settings.OutboundMode.ToLowerInvariant();
            request = request with { RuntimeYamlContent = ConfigureOutbound(request.RuntimeYamlContent, null, currentMode) };
            await ApplyPreparedConfigAsync(request, outboundInterface, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            SetOutboundInterfaceUnavailable(false);
        }
    }

    private static AppSettings LoadOutboundSettings() => new JsonAppSettingsStore(new TrayPlatformDirectories()).Load();

    private async Task<string?> ResolveOutboundInterfaceAsync(AppSettings settings, bool coreIsRunning, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.OutboundInterfaceMode == OutboundInterfaceMode.FollowConfiguration)
        {
            return null;
        }

        string? selected = null;
        try
        {
            var activeTun = coreIsRunning && settings.OutboundInterfaceMode != OutboundInterfaceMode.Physical
                ? await _selectionClient.GetActiveTunDeviceAsync(cancellationToken).ConfigureAwait(false)
                : null;
            selected = SystemOutboundInterfaces.Select(settings, activeTun);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is
            HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException
            or System.Net.NetworkInformation.NetworkInformationException)
        {
            if (!_isOutboundInterfaceUnavailable)
            {
                AppLogger.Warning($"Outbound interface lookup failed: {exception.Message}");
            }
        }

        if (selected is null)
        {
            SetOutboundInterfaceUnavailable(true);
            throw new OutboundInterfaceUnavailableException();
        }

        return selected;
    }

    private void SetOutboundInterfaceUnavailable(bool unavailable)
    {
        if (_isOutboundInterfaceUnavailable == unavailable)
        {
            return;
        }

        _isOutboundInterfaceUnavailable = unavailable;
        UpdateStatus(CurrentStatus.Snapshot);
    }

    private void RememberAppliedOutboundConfig(CoreApplyConfigRequest request, string? outboundInterface)
    {
        _outboundBaseConfig = request;
        _pendingOutboundConfig = null;
        _appliedOutboundInterface = outboundInterface;
        SetOutboundInterfaceUnavailable(false);
    }

    private static string ConfigureOutbound(string content, string? outboundInterface, string? outboundMode = null)
    {
        if (outboundInterface is null && outboundMode is null)
        {
            return content;
        }

        var yaml = new YamlStream();
        using var reader = new StringReader(content);
        yaml.Load(reader);
        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("The runtime configuration root must be a mapping.");
        }

        if (outboundInterface is not null)
        {
            root.Children[new YamlScalarNode("interface-name")] = new YamlScalarNode(outboundInterface);
        }

        if (outboundMode is "rule" or "global" or "direct")
        {
            root.Children[new YamlScalarNode("mode")] = new YamlScalarNode(outboundMode);
        }
        using var writer = new StringWriter();
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }
}
