using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Localization;
using SBproxy.Application.Overrides;
using SBproxy.Domain.Overrides;
using SBproxy.Application.Platform;
using SBproxy.Application.Proxies;
using SBproxy.Domain.Proxies;
using SBproxy.Application.Rules;
using SBproxy.Domain.Rules;
using SBproxy.Application.Runtime;
using SBproxy.Application.Settings;
using SBproxy.Application.Updates;
using SBproxy.Desktop.Services;
using SBproxy.Infrastructure.Core;
using SBproxy.Infrastructure.DataManagement;
using SBproxy.Infrastructure.Diagnostics;
using SBproxy.Infrastructure.Localization;
using SBproxy.Infrastructure.Overrides;
using SBproxy.Infrastructure.Platform;
using SBproxy.Infrastructure.Proxies;
using SBproxy.Infrastructure.Rules;
using SBproxy.Infrastructure.Runtime;
using SBproxy.Infrastructure.Settings;
using SBproxy.Infrastructure.Subscriptions;
using SBproxy.Infrastructure.Updates;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Subscriptions;
using SBproxy.Desktop.Controls;
using SBproxy.Desktop.Debug;
using SBproxy.Desktop.Localization;
using SBproxy.Native;
using SBproxy.Native.Hub;
using SBproxy.Presentation.ViewModels;

namespace SBproxy.Desktop;

public sealed partial class App : Avalonia.Application
{
    private readonly MacOSApplicationMenuService _macOSApplicationMenuService = new();
    private DispatcherTimer? _homeRuntimeTimer;
    private static readonly TimeSpan InitialServiceModeTunWaitTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan InitialServiceModeStatusPollInterval = TimeSpan.FromMilliseconds(250);

    public override void Initialize()
    {
        AppLogger.Debug("Loading Avalonia XAML");
        AvaloniaXamlLoader.Load(this);
        _macOSApplicationMenuService.Initialize(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            var startupStartedAt = Stopwatch.GetTimestamp();
            LogStartupTrace("Framework initialization started", startupStartedAt);
#endif
            AppLogger.Info("Creating main window");
            var platformDirectories = new DesktopPlatformDirectories();
            // 代理组图标磁盘缓存，进程重启后免重下。
            RemoteImageCache.Configure(Path.Combine(platformDirectories.AppDataDirectory, "icon-cache"));
            var settingsStore = new AgentAppSettingsStore(AgentStartupCoordinator.PipeName);
            var settings = settingsStore.Load();
#if DEBUG
            LogStartupTrace($"Settings loaded silent={settings.IsSilentStartEnabled} tun={settings.IsTunEnabled}", startupStartedAt);
#endif
            var updateChecker = new AgentAppUpdateChecker(
                AgentStartupCoordinator.PipeName,
                () => settingsStore.Load().AppUpdateChannel);
            var systemProxyPlatform = CurrentSystemProxyPlatform();
            IUwpLoopbackService uwpLoopbackService = OperatingSystem.IsWindows()
                ? new WindowsUwpLoopbackService()
                : new UnsupportedUwpLoopbackService();
            var systemProxyHostDetector = new NetworkInterfaceSystemProxyHostDetector();
            var agentSystemProxyService =
                new AgentSystemProxyService(AgentStartupCoordinator.PipeName);
            ISystemProxyService systemProxyService = agentSystemProxyService;
            IServiceModeManager serviceModeManager = new AgentServiceModeManager(
                AgentStartupCoordinator.PipeName);
            var networkConnectionProbe = new SystemNetworkConnectionProbe();
            var processPrivilegeProbe = new SystemProcessPrivilegeProbe();
            IAppBehaviorService appBehaviorService = CreateAppBehaviorService();
            IGlobalHotkeyService globalHotkeyService = new AgentGlobalHotkeyService();
            var initialLanguage = AppLanguageParser.Parse(settings.Language);
            var localization = new JsonLocalizationService(initialLanguage);
            LocalizationManager.Initialize(localization);
            var subscriptionDataStore =
                new AgentSubscriptionDataStore(AgentStartupCoordinator.PipeName);
            ISubscriptionStore subscriptionStore = subscriptionDataStore;
            ISubscriptionSelectionStore subscriptionSelectionStore = subscriptionDataStore;
            IProxySelectionStore proxySelectionStore = subscriptionDataStore;
            var overrideStore = new AgentOverrideStore(AgentStartupCoordinator.PipeName);
            var sourceClient = new AgentSourceClient(AgentStartupCoordinator.PipeName);
            IRemoteOverrideDownloader remoteOverrideDownloader = sourceClient;
            var overrideImporter = new OverrideImporter(overrideStore, remoteOverrideDownloader);
            var overrideUpdater = new OverrideUpdater(overrideStore, remoteOverrideDownloader);
            var overrideDeleter = new OverrideDeleter(overrideStore, subscriptionStore);
            var overrideSelectionUpdater = new SubscriptionOverrideSelectionUpdater(subscriptionStore, overrideStore);
            var localSubscriptionImporter = new LocalSubscriptionFileImporter(
                new LocalSubscriptionImporter(subscriptionStore),
                sourceClient);
            IRemoteSubscriptionDownloader remoteSubscriptionDownloader = sourceClient;
            var subscriptionContentDecryptor = new HubSubscriptionContentDecryptor();
            var remoteSubscriptionImporter = new RemoteSubscriptionImporter(
                subscriptionStore,
                remoteSubscriptionDownloader,
                contentDecryptor: subscriptionContentDecryptor);
            var subscriptionUpdater = new SubscriptionUpdater(
                subscriptionStore,
                remoteSubscriptionDownloader,
                contentDecryptor: subscriptionContentDecryptor);
            var runtimeStore = new AgentRuntimeDataStore(
                AgentStartupCoordinator.PipeName,
                subscriptionSelectionStore);
            var selectedSubscriptionRuntimeGenerator = new SelectedSubscriptionRuntimeGenerator(
                subscriptionStore,
                subscriptionSelectionStore,
                new RuntimeConfigGenerator(new HubOverrideEngine()),
                overrideStore,
                runtimeStore);
            var subscriptionDeleter = new SubscriptionDeleter(subscriptionStore, subscriptionSelectionStore, runtimeStore);
            // Provider 同步和状态读取始终走核心管道，保持 Debug 和 Release 路径一致。
            var coreProviderClient = new AgentControlledProviderClient(
                new PipeCoreProviderClient(AgentStartupCoordinator.MihomoPipe),
                AgentStartupCoordinator.PipeName);
            var providerCatalogLoader = new SelectedSubscriptionProviderCatalogLoader(
                subscriptionStore,
                subscriptionSelectionStore,
                new SubscriptionProviderParser(),
                coreProviderClient,
                coreProviderClient);
            ISubscriptionProviderUploader subscriptionProviderUploader = sourceClient;
            ISubscriptionFileOpener subscriptionFileOpener = new DesktopSubscriptionFileOpener(subscriptionStore.GetContentPath);
            IOverrideFileOpener overrideFileOpener = new DesktopOverrideFileOpener(overrideStore.GetContentPath);
            var clipboardWriter = new DesktopClipboardWriter(desktop);
            var chainProxyContextLoader = new SubscriptionChainProxyContextLoader(subscriptionStore, new HubOverrideEngine(), overrideStore);
            var subscriptionPage = new SubscriptionPageViewModel(
                localSubscriptionImporter,
                remoteSubscriptionImporter,
                subscriptionUpdater,
                subscriptionStore,
                overrideStore,
                overrideSelectionUpdater,
                clipboardWriter,
                subscriptionFileOpener,
                subscriptionProviderUploader,
                providerCatalogLoader,
                subscriptionDeleter,
                subscriptionSelectionStore,
                localization,
                chainProxyContextLoader.Load);
            var overridePage = new OverridePageViewModel(
                overrideStore,
                overrideImporter,
                overrideUpdater,
                overrideDeleter,
                sourceClient,
                overrideFileOpener,
                localization);
#if DEBUG
            var pipeProxyCoreClient = new PipeCoreProxyClient(AgentStartupCoordinator.MihomoPipe);
            IProxyCoreClient proxyCoreReader = new ProxyCoreClient(pipeProxyCoreClient);
#else

            IProxyCoreClient proxyCoreReader = new PipeCoreProxyClient(AgentStartupCoordinator.MihomoPipe);
#endif
            IProxyCoreClient proxyCoreClient = new AgentControlledProxyCoreClient(
                proxyCoreReader,
                AgentStartupCoordinator.PipeName);
            IProxyDelayTester proxyDelayTester = new AgentProxyDelayTester(
                AgentStartupCoordinator.PipeName,
                () => settings.DelayTestUrl,
                5000);

            var initialServiceModeStatus = GetInitialServiceModeStatus(serviceModeManager, settings.IsTunEnabled);
#if DEBUG
            LogStartupTrace($"Initial service status ready state={initialServiceModeStatus.State}", startupStartedAt);
#endif
            var coreManager = new AgentIpcClient(AgentStartupCoordinator.PipeName);
            var coreUpdater = new AgentCoreUpdater(AgentStartupCoordinator.PipeName);
            var connectionPage = new ConnectionPageViewModel(proxyCoreClient, localization: localization);
            var proxyConfigSource = runtimeStore;
            var proxyConfigParser = new ProxyConfigParser();
            var proxyConfigLoader = new ProxyConfigLoader(
                proxyConfigSource,
                proxyConfigParser,
                () => true);
            var proxySelectionSyncState = new ProxySelectionSyncState();
            var mihomoApiProxyConfigProvider = new MihomoApiProxyConfigProvider(
                proxyCoreClient,
                new FileRuntimeProxyGroupIconProvider(proxyConfigSource, proxyConfigParser));
            var primaryProxyConfigProvider = new StoredProxySelectionConfigProvider(
                mihomoApiProxyConfigProvider,
                proxySelectionStore,
                subscriptionSelectionStore,
                proxySelectionSyncState,
                importCoreSelections: true,
                pruneInvalidSelections: false);
            var fallbackProxyConfigProvider = new StoredProxySelectionConfigProvider(
                new FileRuntimeProxyConfigProvider(proxyConfigLoader),
                proxySelectionStore,
                subscriptionSelectionStore,
                pruneInvalidSelections: false);
            var proxySelectionRestorer = new ProxySelectionRestorer(
                proxyCoreClient,
                mihomoApiProxyConfigProvider,
                primaryProxyConfigProvider,
                proxySelectionSyncState);
            var proxyNodeSortMode = Enum.TryParse<ProxyNodeSortMode>(settings.ProxyNodeSortMode, ignoreCase: true, out var parsedProxyNodeSortMode)
                ? parsedProxyNodeSortMode
                : ProxyNodeSortMode.Default;
            var proxyPage = new ProxyPageViewModel(
                proxyConfigLoader,
                new ProxyDelayService(proxyDelayTester),
                proxyCoreClient,
                primaryProxyConfigProvider,
                fallbackProxyConfigProvider,
                localization,
                new ProxySelectionService(proxyCoreClient, proxySelectionStore, subscriptionSelectionStore),
                new ProxyNodeShareService(proxyConfigSource),
                clipboardWriter,
                initialSortMode: proxyNodeSortMode,
                persistSortMode: sortMode =>
                {
                    settings.ProxyNodeSortMode = sortMode.ToString();
                    settingsStore.Save(settings);
                },
                isPresentationActive: false);
            var rulePage = new RulePageViewModel(new RuleListLoader(
                runtimeStore,
                new RuleParser(),
                () => true),
                localization);
            var coreLogPage = new CoreLogPageViewModel(localization: localization);
            var dataBackupService = new AgentDataManagementService(
                AgentStartupCoordinator.PipeName);
            var webDavDataBackupService = dataBackupService;
            var viewModel = new MainWindowViewModel(
                settingsStore,
                localization,
                proxyPage,
                connectionPage,
                coreLogPage,
                subscriptionPage,
                overridePage,
                rulePage,
                dataManagementService: dataBackupService,
                webDavDataBackupService: webDavDataBackupService,
                updateChecker: updateChecker,
                uwpLoopbackService: uwpLoopbackService,
                systemProxyHostDetector: systemProxyHostDetector,
                systemProxyService: systemProxyService,
                serviceModeManager: serviceModeManager,
                isServiceModeCoreHostActive: () => settingsStore.Load().IsTunEnabled,
                systemProxyRequestFactory: () => SystemProxyApplicationRequest.Build(settingsStore.Load(), systemProxyPlatform),
                appBehaviorService: appBehaviorService,
                globalHotkeyService: globalHotkeyService,
                runtimeFallbackGenerator: new SelectedRuntimeFallbackGenerator(
                    subscriptionStore,
                    overrideSelectionUpdater,
                    selectedSubscriptionRuntimeGenerator),
                runtimeStore: runtimeStore,
                coreManager: coreManager,
                initialSettings: settings,
                windowEffectCapability: new WindowEffectCapability(),
                networkConnectionProbe: networkConnectionProbe,
                homeProxyClient: proxyCoreClient,
                coreUpdater: coreUpdater,
                processPrivilegeProbe: processPrivilegeProbe,
                initialServiceModeStatus: initialServiceModeStatus,
                systemPlatform: systemProxyPlatform,
                clipboardWriter: clipboardWriter,
                serviceModeSessionActivator: token => AgentStartupCoordinator.SetTunHostAsync(true, token),
                serviceModeSessionDeactivator: token => AgentStartupCoordinator.SetTunHostAsync(false, token),
                appLogReader: new FileAppLogReader(DesktopApplicationLayout.RunningLogFilePath),
                appLogExporter: new FileAppLogExporter(DesktopApplicationLayout.RunningLogFilePath));
            viewModel.HomePage.ApplySystemProxyState(agentSystemProxyService.IsEnabled());
#if DEBUG
            LogStartupTrace("Main view model created", startupStartedAt);
#endif
            StartHomeRuntimeTimer(viewModel);
            var mainWindow = new MainWindow(settingsStore, settings)
            {
                DataContext = viewModel
            };
#if DEBUG
            LogStartupTrace("Main window constructed and bound", startupStartedAt);
#endif

            // 进入 --agent-ui 就代表 Agent 已明确要求显示 UI；静默自启动只启动 Agent。
            desktop.MainWindow = mainWindow;

            var isMacOSShutdownScheduled = false;
            desktop.ShutdownRequested += (_, args) =>
            {
                if (!OperatingSystem.IsMacOS())
                {
                    return;
                }

                args.Cancel = true;
                if (isMacOSShutdownScheduled)
                {
                    return;
                }

                isMacOSShutdownScheduled = true;
                Dispatcher.UIThread.Post(() =>
                {
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    mainWindow.RequestShutdown();
                    desktop.Shutdown(0);
                });
            };
            desktop.Exit += (_, _) =>
            {
                StopBackgroundServices();
                AgentStartupCoordinator.UnregisterUiAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                _macOSApplicationMenuService.Dispose();
                globalHotkeyService.Dispose();
                viewModel.Dispose();
                runtimeStore.Dispose();
                sourceClient.Dispose();
                overrideStore.Dispose();
                subscriptionDataStore.Dispose();
                settingsStore.Dispose();
                DisposeOwnedServices(coreManager, proxyCoreClient, proxyDelayTester, coreProviderClient, coreUpdater, updateChecker, dataBackupService, serviceModeManager);
            };
            _ = AgentStartupCoordinator.RegisterUiAsync(
                () => Dispatcher.UIThread.Post(() => ShowMainWindow(desktop, mainWindow)),
                () => Dispatcher.UIThread.Post(() =>
                {
                    mainWindow.RequestShutdown();
                    desktop.TryShutdown(0);
                }),
                version => Dispatcher.UIThread.Post(() =>
                    viewModel.Update.ApplyAutoCheckResult(new AppUpdateAutoCheckResult(
                        true,
                        true,
                        $"New version available: {version}"))));
            _macOSApplicationMenuService.Attach(
                localization,
                () =>
                {
                    ShowMainWindow(desktop, mainWindow);
                    if (viewModel.CurrentPage != SBproxy.Presentation.ViewModels.NavigationPage.Settings)
                    {
                        viewModel.ShowSettingsCommand.Execute(null);
                    }
                    viewModel.Settings.ShowAboutCommand.Execute(null);
                },
                () =>
                {
                    ShowMainWindow(desktop, mainWindow);
                    viewModel.ShowSettingsCommand.Execute(null);
                },
                () =>
                {
                    _ = AgentStartupCoordinator.ExitApplicationAsync();
                    mainWindow.RequestShutdown();
                    desktop.TryShutdown(0);
                });
            foreach (var (action, gesture) in new[]
            {
                (GlobalHotkeyAction.ToggleWindow, settings.WindowToggleHotkey),
                (GlobalHotkeyAction.ToggleSystemProxy, settings.SystemProxyToggleHotkey),
                (GlobalHotkeyAction.ToggleTun, settings.TunToggleHotkey),
            })
            {
                var hotkeyResult = globalHotkeyService.Apply(action, gesture);
                if (!hotkeyResult.IsSuccess)
                {
                    AppLogger.Warning($"Global hotkey startup registration failed: action={action} error={hotkeyResult.Error}");
                }
            }
#if DEBUG
            LogStartupTrace("Tray service attached", startupStartedAt);
#endif
#if DEBUG
            DebugCommands.Start(mainWindow);
#endif
            // 首屏出现后再启动重活，保持窗口启动响应。
            Dispatcher.UIThread.Post(
                () =>
                {
#if DEBUG
                    LogStartupTrace("Background startup dispatch entered", startupStartedAt);
#endif
                    _ = subscriptionPage.InitializeAsync();
                    _ = overridePage.InitializeAsync();
                    _ = StartCoreServicesAsync(coreManager, viewModel, proxyPage, rulePage, proxySelectionRestorer);
                },
                DispatcherPriority.Background);
#if DEBUG
            LogStartupTrace("Framework initialization completed", startupStartedAt);
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StopBackgroundServices()
    {
        StopTimer(ref _homeRuntimeTimer);
    }

    private static void StopTimer(ref DispatcherTimer? timer)
    {
        if (timer is null)
        {
            return;
        }

        timer.Stop();
        timer = null;
    }

    private static void DisposeOwnedServices(params object?[] services)
    {
        foreach (var service in services)
        {
            if (service is not IDisposable disposable)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                AppLogger.Warning($"Service dispose failed: {exception.Message}");
            }
        }
    }

    private static void ShowMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window)
    {
        MacOSDockVisibilityService.SetVisible(true);
        if (desktop.MainWindow is null)
        {
            desktop.MainWindow = window;
        }
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        var wasMinimized = window.WindowState == WindowState.Minimized;
        window.Show();
        if (wasMinimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }

    private async Task StartCoreServicesAsync(
        AgentIpcClient coreManager,
        MainWindowViewModel viewModel,
        ProxyPageViewModel proxyPage,
        RulePageViewModel rulePage,
        ProxySelectionRestorer proxySelectionRestorer)
    {
        try
        {
            await coreManager.ConnectAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Core manager startup failed: {exception.Message}");
            viewModel.ShowErrorToast(LocalizationManager.Translate("Common.Error.CoreStartupFailed"));
        }

        try
        {
            await proxySelectionRestorer.RestoreCurrentSubscriptionAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Startup proxy selection restore failed: {exception.Message}");
        }

        try
        {
            await proxyPage.RefreshProxiesAsync();
            if (viewModel.SubscriptionPage.CurrentSubscriptionId is { } subscriptionId)
            {
                proxyPage.BindLoadedConfigToSubscription(subscriptionId);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Startup proxy list refresh failed: {exception.Message}");
        }

        RefreshRulesForStartup(rulePage);
    }

    internal static void RefreshRulesForStartup(RulePageViewModel rulePage)
    {
        try
        {
            rulePage.RefreshRulesCommand.Execute(null);
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Startup rule list refresh failed: {exception.Message}");
        }
    }

    private static ServiceModeStatus GetInitialServiceModeStatus(IServiceModeManager serviceModeManager, bool waitForTunService)
    {
#if DEBUG
        var startedAt = Stopwatch.GetTimestamp();
        AppLogger.Info($"[StartupTrace] Service status probing started waitForTun={waitForTunService}");
#endif
        var status = ProbeServiceModeStatus(serviceModeManager);
        if (!waitForTunService || status.IsRunning || !status.IsInstalled)
        {
#if DEBUG
            AppLogger.Info($"[StartupTrace] Service status probing completed elapsed={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0}ms polls=1 state={status.State}");
#endif
            return status;
        }

        // TUN 启动依赖服务核心；登录瞬间服务可能仍在 SCM 启动路径上。
        var stopwatch = Stopwatch.StartNew();
#if DEBUG
        var pollCount = 1;
#endif
        while (stopwatch.Elapsed < InitialServiceModeTunWaitTimeout)
        {
            Thread.Sleep(InitialServiceModeStatusPollInterval);
            status = ProbeServiceModeStatus(serviceModeManager);
#if DEBUG
            pollCount++;
#endif
            if (status.IsRunning || !status.IsInstalled)
            {
#if DEBUG
                AppLogger.Info($"[StartupTrace] Service status probing completed elapsed={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0}ms polls={pollCount} state={status.State}");
#endif
                return status;
            }
        }

#if DEBUG
        AppLogger.Info($"[StartupTrace] Service status probing timed out elapsed={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0}ms polls={pollCount} state={status.State}");
#endif
        return status;
    }

    private static ServiceModeStatus ProbeServiceModeStatus(IServiceModeManager serviceModeManager)
    {
#if DEBUG
        var startedAt = Stopwatch.GetTimestamp();
#endif
        try
        {
            var status = serviceModeManager.GetStatusAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
#if DEBUG
            AppLogger.Info($"[StartupTrace] Service status probe elapsed={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0}ms state={status.State}");
#endif
            return status;
        }
        catch (Exception exception)
        {
#if DEBUG
            AppLogger.Info($"[StartupTrace] Service status probe failed elapsed={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0}ms");
#endif
            AppLogger.Warning($"Service-mode status probe failed: {exception.Message}");
            return ServiceModeStatus.Unavailable(exception.Message);
        }
    }

#if DEBUG
    private static void LogStartupTrace(string stage, long startedAt)
    {
        AppLogger.Info($"[StartupTrace] {stage} elapsed={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0.0}ms");
    }
#endif

    private void StartHomeRuntimeTimer(MainWindowViewModel viewModel)
    {
        _homeRuntimeTimer = new DispatcherTimer
        {
            // 主页运行状态每秒刷新；后台页面跳过实时轮询。
            Interval = TimeSpan.FromSeconds(1)
        };
        _homeRuntimeTimer.Tick += (_, _) => viewModel.OnHomeRuntimeTick();
        _homeRuntimeTimer.Start();
        AppLogger.Info("Home runtime refresh started");
    }

    private static SystemProxyPlatform CurrentSystemProxyPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return SystemProxyPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return SystemProxyPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return SystemProxyPlatform.MacOS;
        }

        return SystemProxyPlatform.Other;
    }

    private static IAppBehaviorService CreateAppBehaviorService()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsAppBehaviorService();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxAppBehaviorService();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOSAppBehaviorService();
        }

        return new UnsupportedAppBehaviorService();
    }

}
