using System.Text.Json;
using Avalonia.Media.Imaging;
using Stelliberty.Application.Cli;
using Stelliberty.Application.Platform;
using Stelliberty.Application.Proxies;
using Stelliberty.Application.Runtime;
using Stelliberty.Application.Settings;
using Stelliberty.Application.Subscriptions;
using Stelliberty.Domain.Proxies;
using Stelliberty.Infrastructure.Platform;
using Stelliberty.Infrastructure.Proxies;
using Stelliberty.Infrastructure.Rules;
using Stelliberty.Infrastructure.Runtime;
using Stelliberty.Infrastructure.Settings;
using Stelliberty.Infrastructure.Subscriptions;
using Stelliberty.Infrastructure.Tray;
using Stelliberty.Native.Hub;

namespace Stelliberty.Tray.Cli;

// CLI 与 UI 共用持久数据和后台运行时；此服务不创建窗口或 ViewModel。
internal sealed class CliService(
    ITrayCoreRuntime runtime,
    ISystemProxyController systemProxy,
    IProxyDelayResultSink delayResults,
    BackgroundOperationRegistry operations,
    Action<string> notifyStateChanged) : IDisposable
{
    private readonly JsonAppSettingsStore _settings = new(new TrayPlatformDirectories());
    private readonly FileSubscriptionStore _subscriptions = new(TrayApplicationLayout.AppDataDirectory);
    private readonly FileSubscriptionSelectionStore _selection = new(TrayApplicationLayout.AppDataDirectory);
    private readonly PipeCoreProxyClient _proxy = new(TrayCoreEndpoints.Core);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _stateChanged;

    public async Task<CliResponse> ExecuteAsync(CliRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(170));
        var entered = false;
        string? command = null;
        var owner = Guid.NewGuid();
        Guid? lease = null;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            _stateChanged = false;
            if (!_settings.Load().IsCliEnabled)
                return new(false, "disabled", Error: "Enable command-line control manually in Settings > App behavior.");
            var call = CliCommands.Parse(request.Arguments);
            command = call.Command;
            var resource = call.Command.StartsWith("subscription ", StringComparison.Ordinal) && call.Command != "subscription list"
                ? BackgroundOperationRequest.Subscriptions
                : call.Command == "proxy delay" ? BackgroundOperationRequest.Delay : null;
            if (resource is not null)
            {
                lease = operations.TryAcquire(owner, new(resource, call.Values.Take(1).ToArray(), call.Command == "subscription update"));
                if (lease is null) throw new CliOperationException("unavailable", "Another client is performing this operation. Try again after it completes.");
            }
            if (call.Command.StartsWith("settings ", StringComparison.Ordinal)
                || call.Command.StartsWith("dns ", StringComparison.Ordinal)
                || call.Command.StartsWith("proxy config ", StringComparison.Ordinal))
                return await SettingAsync(call, timeout.Token).ConfigureAwait(false);
            if (call.Command.StartsWith("subscription ", StringComparison.Ordinal))
                return await SubscriptionAsync(call, timeout.Token).ConfigureAwait(false);
            return await ProxyAsync(call, timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException exception) { return new(false, "invalid_arguments", Error: exception.Message); }
        catch (JsonException) { return new(false, "invalid_arguments", Error: "Invalid JSON value or command payload."); }
        catch (OperationCanceledException) { return new(false, "timeout", Error: "Operation timed out; inspect state before retrying."); }
        catch (CliOperationException exception) { return new(false, exception.Status, Error: exception.Message); }
        // Business exceptions may contain credential-bearing URLs; never echo them to the caller.
        catch (Exception) { return new(false, "operation_failed", Error: "Operation failed. Settings may have been saved; inspect state before retrying."); }
        finally
        {
            if (lease is { } id) operations.Release(owner, id);
            if (entered && _stateChanged && command is not null) notifyStateChanged(command);
            if (entered) _gate.Release();
        }
    }

    private async Task<CliResponse> SettingAsync(CliInvocation call, CancellationToken token)
    {
        var separator = call.Command.LastIndexOf(' ');
        var group = call.Command[..separator];
        var verb = call.Command[(separator + 1)..];
        if (verb == "schema") return Ok(CliCatalog.Settings.Where(item => item.Group == group).Select(item => item.Schema).ToArray());
        var settings = _settings.Load();
        if (verb == "get")
        {
            if (call.Values.Length == 1)
            {
                var field = CliCatalog.Find(group, call.Values[0]);
                return Ok(new { key = field.Name, value = CliSettings.Read(settings, field) });
            }
            return Ok(CliCatalog.Settings.Where(item => item.Group == group)
                .ToDictionary(item => item.Name, item => CliSettings.Read(settings, item)));
        }
        var setting = CliCatalog.Find(group, call.Values[0]);
        var parsed = setting.Parse(call.Values[1]);
        if (setting.Name == "window-effect" && !new WindowEffectCapability().SupportedEffects
            .Contains(Enum.Parse<WindowEffect>((string)parsed, true)))
            throw new ArgumentException("window-effect is not supported on this platform.");
        if (setting.Name == "background-image" && (string)parsed is { Length: > 0 } path && path != AppSettings.DefaultBackgroundImagePath)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("background-image requires an absolute path.");
            using var bitmap = new Bitmap(path);
        }
        var oldValue = CliSettings.Read(settings, setting);
        CliSettings.Assign(settings, setting, parsed);
        if (call.DryRun) return new(true, "dry_run", new { key = setting.Name, old_value = oldValue, schema = setting.Schema });
        _stateChanged = true;
        if (setting.Name == "silent-start" && settings.IsAutoStartEnabled)
        {
            var binary = Path.Combine(AppContext.BaseDirectory, AppRuntimeNames.TrayBinaryName);
            IAppBehaviorService behavior = OperatingSystem.IsWindows() ? new WindowsAppBehaviorService(binary)
                : OperatingSystem.IsLinux() ? new LinuxAppBehaviorService(binary)
                : OperatingSystem.IsMacOS() ? new MacOSAppBehaviorService(binary, Path.Combine(AppContext.BaseDirectory, "tweaks", "cli", "stelliberty-cli"))
                : new UnsupportedAppBehaviorService();
            behavior.Apply(new(settings.IsSilentStartEnabled, true));
        }
        var property = typeof(AppSettings).GetProperty(setting.SettingProperty)!;
        var expected = JsonSerializer.Serialize(property.GetValue(settings));
        _settings.Save(settings);
        if (JsonSerializer.Serialize(property.GetValue(_settings.Load())) != expected)
            throw new CliOperationException("operation_failed", "The application normalized the value; read the setting to inspect its current value.");
        var status = group == "settings" || setting.Name == "delay-test-url" ? "saved" : await ApplyRuntimeAsync(token).ConfigureAwait(false);
        return Saved(status, new { key = setting.Name, old_value = oldValue, value = CliSettings.Read(_settings.Load(), setting) });
    }

    private async Task<string> ApplyRuntimeAsync(CancellationToken token)
    {
        var selected = _selection.GetCurrentSubscriptionId();
        try
        {
            var applied = await runtime.RefreshCurrentSettingsAsync(token).ConfigureAwait(false);
            if (selected is not null && _selection.GetCurrentSubscriptionId() != selected)
                return "saved_not_applied";
            if (applied is null) return "saved";
            var proxy = await systemProxy.GetStatusAsync(token).ConfigureAwait(false);
            if (proxy.IsEnabled && proxy.IsOwned)
            {
                var result = await systemProxy.SetEnabledAsync(true,
                    SystemProxyApplicationRequest.Build(_settings.Load(), Platform), token).ConfigureAwait(false);
                if (!result.IsSuccess) return "saved_not_applied";
            }
            return "applied";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return "saved_not_applied"; }
    }

    private async Task<CliResponse> ProxyAsync(CliInvocation call, CancellationToken token)
    {
        var settings = _settings.Load();
        if (call.Command == "proxy mode get")
            return Ok(new { mode = (await _proxy.GetOutboundModeAsync(token).ConfigureAwait(false))?.ToString().ToLowerInvariant()
                ?? settings.OutboundMode.ToLowerInvariant() });
        if (call.Command == "proxy system get")
            return Ok(new { enabled = (await systemProxy.GetStatusAsync(token).ConfigureAwait(false)).IsEnabled, busy = false });
        if (call.Command == "proxy tun get")
            return Ok(new { enabled = settings.IsTunEnabled, supported = await CanUseTunAsync(token).ConfigureAwait(false), busy = false });
        if (call.Command is "proxy system set" or "proxy tun set")
        {
            if (!bool.TryParse(call.Values[0], out var enabled)) throw new ArgumentException("Expected true or false.");
            if (enabled && !IsCoreRunning) throw new CliOperationException("unavailable", "Start the core before enabling network takeover.");
            var tun = call.Command == "proxy tun set";
            if (tun && (!IsCoreRunning || !await CanUseTunAsync(token).ConfigureAwait(false)))
                throw new CliOperationException("unavailable", "TUN requires a running core and the existing privileged service/admin mode.");
            if (call.DryRun) return new(true, "dry_run", new { enabled });
            _stateChanged = true;
            if (tun)
            {
                var previous = settings.IsTunEnabled;
                settings.IsTunEnabled = enabled;
                _settings.Save(settings);
                try
                {
                    if (await ApplyRuntimeAsync(token).ConfigureAwait(false) != "applied")
                        throw new CliOperationException("operation_failed", "The requested TUN state was not applied.");
                }
                catch
                {
                    settings.IsTunEnabled = previous;
                    _settings.Save(settings);
                    throw;
                }
            }
            else
            {
                var result = await systemProxy.SetEnabledAsync(enabled,
                    enabled ? SystemProxyApplicationRequest.Build(settings, Platform) : null, token).ConfigureAwait(false);
                if (!result.IsSuccess || result.Status.IsEnabled != enabled)
                    throw new CliOperationException("operation_failed", "The requested system proxy state was not applied.");
            }
            return new(true, "applied", new { enabled });
        }
        if (!IsCoreRunning) throw new CliOperationException("unavailable", "An interactive running core is required.");
        if (call.Command == "proxy mode set")
        {
            var mode = call.Values[0].ToLowerInvariant() switch
            {
                "rule" => OutboundMode.Rule, "global" => OutboundMode.Global, "direct" => OutboundMode.Direct,
                _ => throw new ArgumentException("Expected rule, global or direct.")
            };
            if (call.DryRun) return new(true, "dry_run", new { mode = mode.ToString().ToLowerInvariant() });
            _stateChanged = true;
            if (!await _proxy.SetOutboundModeAsync(mode, token).ConfigureAwait(false))
                throw new CliOperationException("operation_failed", "Routing mode was not applied.");
            settings.OutboundMode = mode.ToString();
            _settings.Save(settings);
            return new(true, "applied", new { mode = mode.ToString().ToLowerInvariant() });
        }
        var config = await new MihomoApiProxyConfigProvider(_proxy).LoadAsync(token).ConfigureAwait(false);
        if (call.Command == "proxy groups") return Ok(config.VisibleGroups.Select(entry => new
            { entry.Name, entry.Type, selected = entry.DisplaySelectionName, selectable = entry.IsManualSelectable }).ToArray());
        var groupName = call.RequireOption("group");
        var group = config.VisibleGroups.FirstOrDefault(item => item.Name == groupName)
            ?? throw new ArgumentException("Group not found; use proxy groups.");
        var nodeName = call.Options.GetValueOrDefault("node");
        if (call.Command == "proxy select") nodeName = call.RequireOption("node");
        if (nodeName is not null && !group.All.Contains(nodeName)) throw new ArgumentException("Node not found in this group.");
        if (call.Command == "proxy select" && !group.IsManualSelectable) throw new ArgumentException("This group does not support manual selection.");
        if (call.DryRun) return new(true, "dry_run", new { group = groupName, node = nodeName });
        var selections = new ProxySelectionService(_proxy, new FileProxySelectionStore(TrayApplicationLayout.AppDataDirectory), _selection);
        if (call.Command == "proxy select")
        {
            _stateChanged = true;
            var selected = await selections.SelectNodeAsync(config, groupName, nodeName!, true, token).ConfigureAwait(false);
            if (selected is null) throw new CliOperationException("operation_failed", "Node selection was not applied.");
            return new(true, "applied", new { group = groupName, node = nodeName });
        }
        if (call.Command == "proxy delay")
        {
            _stateChanged = true;
            using var tester = new PipeCoreProxyDelayTester(TrayCoreEndpoints.Core, () => _settings.Load().DelayTestUrl, 5000);
            var service = new ProxyDelayService(tester, delayResults);
            var result = nodeName is null
                ? await service.TestGroupAsync(config, groupName, token).ConfigureAwait(false)
                : await service.TestNodeAsync(config, nodeName, token).ConfigureAwait(false);
            config = result.Config;
            if (nodeName is null)
                config = (await selections.ReleaseFixedSelectionsAsync(config, [groupName], true, token).ConfigureAwait(false)).Config;
            group = config.Groups.First(item => item.Name == groupName);
        }
        return Ok(group.All.Where(name => nodeName is null || name == nodeName).Select(name =>
        {
            config.TryGetEntryDelay(name, out var delay);
            var type = config.Nodes.TryGetValue(name, out var node) ? node.Type
                : config.Groups.FirstOrDefault(item => item.Name == name)?.Type ?? "Unknown";
            return new { name, type, selected = group.DisplaySelectionName == name,
                delay_ms = delay, delay_state = delay is null ? "untested" : delay < 0 ? "failed" : "tested" };
        }).ToArray());
    }

    private async Task<CliResponse> SubscriptionAsync(CliInvocation call, CancellationToken token)
    {
        var current = _selection.GetCurrentSubscriptionId();
        var subscriptions = _subscriptions.LoadSubscriptions();
        if (call.Command == "subscription list") return Ok(subscriptions.Select(entry => new
            { entry.Id, entry.Name, entry.IsLocalFile, IsCurrent = entry.Id == current, IsUpdating = operations.Snapshot.Any(operation => operation.Request.IsUpdate
                    && operation.Request.SubscriptionIds.Contains(entry.Id)),
                entry.LastUpdatedAt, HasError = !string.IsNullOrEmpty(entry.LastError) }).ToArray());
        var downloader = new HttpRemoteSubscriptionDownloader(() =>
        {
            var settings = _settings.Load();
            return (settings.ProxyHost, settings.MixedPort);
        });
        if (call.Command == "subscription add")
        {
            var name = call.RequireOption("name");
            var remote = call.Options.TryGetValue("url", out var url);
            var local = call.Options.TryGetValue("file", out var file);
            if (remote == local) throw new ArgumentException("Provide exactly one of --url or --file.");
            if (remote) CliSettings.ValidateHttpUrl(url!);
            else if (!Path.IsPathFullyQualified(file!) || !File.Exists(file)) throw new ArgumentException("Local subscription must be an existing absolute file path.");
            if (call.DryRun) return new(true, "dry_run", new { name, source_kind = remote ? "remote" : "local" });
            _stateChanged = true;
            var added = remote
                ? await new RemoteSubscriptionImporter(_subscriptions, downloader, contentDecryptor: new HubSubscriptionContentDecryptor())
                    .ImportAsync(new(name, url!), token).ConfigureAwait(false)
                : new LocalSubscriptionFileImporter(new LocalSubscriptionImporter(_subscriptions), new FileLocalSubscriptionFileReader()).Import(new(name, file!));
            if (string.IsNullOrWhiteSpace(current)) _selection.SetCurrentSubscriptionId(added.Id);
            var isCurrent = _selection.GetCurrentSubscriptionId() == added.Id;
            return Saved(isCurrent ? await ApplyRuntimeAsync(token).ConfigureAwait(false) : "saved",
                new { added.Id, added.Name, IsCurrent = isCurrent });
        }
        var id = call.Values[0];
        var item = subscriptions.FirstOrDefault(entry => entry.Id == id) ?? throw new ArgumentException("Unknown subscription ID.");
        if (call.Command == "subscription update" && item.IsLocalFile) throw new ArgumentException("Only remote subscriptions can be updated.");
        if (call.DryRun) return new(true, "dry_run", new { item.Id, item.Name, IsCurrent = id == current });
        _stateChanged = true;
        switch (call.Command)
        {
            case "subscription update":
                var result = await new SubscriptionUpdater(_subscriptions, downloader, contentDecryptor: new HubSubscriptionContentDecryptor())
                    .UpdateAsync(id, token).ConfigureAwait(false);
                if (!result.UpdatedSubscriptionIds.Contains(id))
                    throw new CliOperationException("operation_failed", "Subscription update did not complete successfully. Source details are redacted.");
                break;
            case "subscription select":
                _selection.SetCurrentSubscriptionId(id);
                break;
            case "subscription delete":
                new SubscriptionDeleter(_subscriptions, _selection, new FileRuntimeConfigStore(TrayApplicationLayout.RuntimeDirectory),
                    new FileRuleOverrideStore(TrayApplicationLayout.AppDataDirectory), new FileProxySelectionStore(TrayApplicationLayout.AppDataDirectory)).Delete(id);
                break;
        }
        return Saved(id == current || call.Command == "subscription select"
            ? await ApplyRuntimeAsync(token).ConfigureAwait(false) : "saved", new { id });
    }

    private bool IsCoreRunning => runtime.CurrentStatus.Snapshot.State == CoreState.Running;
    private async Task<bool> CanUseTunAsync(CancellationToken token) => AppSettingsNormalizer.CanUseTun(
        new SystemProcessPrivilegeProbe().Detect(), (await runtime.GetServiceModeStatusAsync(token).ConfigureAwait(false)).IsRunning);
    private static SystemProxyPlatform Platform => OperatingSystem.IsWindows() ? SystemProxyPlatform.Windows
        : OperatingSystem.IsMacOS() ? SystemProxyPlatform.MacOS : OperatingSystem.IsLinux() ? SystemProxyPlatform.Linux : SystemProxyPlatform.Other;
    private static CliResponse Ok(object? data) => new(true, "ok", data);
    private static CliResponse Saved(string status, object data) => new(status != "saved_not_applied", status, data,
        status == "saved_not_applied" ? "Saved, but runtime application failed. Review settings before retrying." : null);
    public void Dispose() => _proxy.Dispose();
}

internal sealed class CliOperationException(string status, string message) : Exception(message)
{
    public string Status { get; } = status;
}
