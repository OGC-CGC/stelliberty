using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
#if DEBUG
using HotAvalonia;
#endif
using Stelliberty.Application.Diagnostics;
using Stelliberty.Application.Platform;
using Stelliberty.Application.Settings;
using Stelliberty.Desktop.Controls;
using Stelliberty.Desktop.Services;
using Stelliberty.Desktop.Views;
using Stelliberty.Desktop.Views.Dialogs;
using Stelliberty.Presentation.ViewModels;
using AppNavigationPage = Stelliberty.Presentation.ViewModels.NavigationPage;
using IconPacks.Avalonia.MingCuteIcons;

namespace Stelliberty.Desktop;

public sealed partial class MainWindow : Window
{
    private const double SidebarAutoCollapseWidth = 1040;
    private const double SidebarEdgeFadeHeight = 36;
    private readonly LinearGradientBrush _sidebarEdgeFadeMask = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Colors.Black, 0),
            new GradientStop(Colors.Black, 1),
            new GradientStop(Colors.Transparent, 1)
        }
    };
    internal bool IsSidebarManuallyCollapsed { get; private set; }

    // 隐藏后等待：轻量模式结束 UI 进程，常规模式回收页面视觉树。
    private static readonly TimeSpan HiddenReleaseDelay = TimeSpan.FromSeconds(AppSettings.DefaultLightweightModeShutdownDelaySeconds);
    private static readonly TimeSpan PageLoadingMinVisible = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan NavColorFadeDuration = TimeSpan.FromMilliseconds(120);
    // 选中态（文字与图标配色）等底板基本到位后再切换。
    private static readonly TimeSpan NavSelectionRevealDelay = TimeSpan.FromMilliseconds(100);
    // 图标形状在配色渐变到一半时切换。
    private static readonly TimeSpan NavIconMorphDelay = NavSelectionRevealDelay + NavColorFadeDuration / 2;
    // 旧项的图标提前回落到线条形态，跑在底板滑动前段。
    private static readonly TimeSpan NavIconUnmorphDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan NavScrollAnimationDuration = TimeSpan.FromMilliseconds(100);
    private readonly WindowAppearanceService _windowAppearanceService = new();
    private readonly MacOSWindowControlsService _macOSWindowControls = new();
    private readonly WindowStateService _windowStateService;
    private readonly SystemAccentColorService _systemAccentColorService = new();
    private readonly BitmapCache _proxyPageBitmapCache = new() { SnapsToDevicePixels = true };
    private readonly Dictionary<AppNavigationPage, ContentControl> _pageHosts = new();
    private readonly Dictionary<AppNavigationPage, Dictionary<string, Vector>> _pageScrollOffsets = new();
    private readonly DispatcherTimer _hiddenReleaseTimer;
    private readonly DispatcherTimer _relativeTimeTimer;
    private DispatcherTimer? _navVisualTimer;
    private IReadOnlyDictionary<SettingsSubPage, Vector>? _settingsScrollOffsets;
    private bool _pageHostsReady;
    private ContentControl? _visiblePageHost;
    private ContentControl? _pendingPageHost;
    private MainWindowViewModel? _attachedViewModel;
    private AccentColorPickerView? _activeAccentPicker;
    // 侧栏选中底板：跟随当前项平移，记录上次锚点以区分“目标移动”和“底板动画中”。
    private Button? _navIndicatorTarget;
    private Rect? _navIndicatorRect;
    // 选中项在视口外时，侧栏先把它滚进来（带动画），底板随滚动逐帧跟随。
    private DispatcherTimer? _navScrollTimer;
    private bool _isShutdownRequested;
    private bool _isShutdownPreparing;
    private bool _shouldShutdownTray = true;
    private WindowDecorationStyle _windowDecorationStyle = WindowDecorationStyleRules.Default;
    private bool _hasOpened;
    private bool _wasFullScreen;
    private long _pageTransitionVersion;
    private long _pageLoadingShownAt;
#if DEBUG
    private long _navigationDebugVersion;
    private long _hotReloadRecoveryVersion;
#endif

    public MainWindow()
        : this(null, null)
    {
    }

    public MainWindow(IAppSettingsStore? settingsStore, AppSettings? settings)
    {
        IsSidebarManuallyCollapsed = settings?.IsSidebarCollapsed ?? true;
        _windowStateService = new WindowStateService(settingsStore, settings);
        _hiddenReleaseTimer = new DispatcherTimer { Interval = HiddenReleaseDelay };
        _hiddenReleaseTimer.Tick += OnHiddenReleaseTimerTick;
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _relativeTimeTimer.Tick += OnRelativeTimeTimerTick;
        InitializeComponent();
        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        ApplyWindowDecorationStyle(settings);
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnWindowPropertyChanged;
        Opened += OnOpened;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        _windowStateService.Attach(this);
        UpdateSidebarState();
        UpdateWindowStateVisuals();
        UpdatePinState();
        UpdateWindowActiveVisuals();
#if DEBUG
        AttachDevBadge();
#endif
    }

    private void ApplyWindowDecorationStyle(AppSettings? settings)
    {
        _windowDecorationStyle = WindowDecorationStyleRules.Parse(settings?.WindowDecorationStyle);
        ApplyWindowDecorations();
    }

    private void ApplyWindowDecorations()
    {
        var isMacOS = OperatingSystem.IsMacOS();
        var useWindowsButtons = _windowDecorationStyle == WindowDecorationStyle.Windows;
        var useMacButtons = _windowDecorationStyle == WindowDecorationStyle.MacOS;
        var useCustomButtons = useWindowsButtons || useMacButtons;
        // macOS 的系统红绿灯浮在客户端区域上，保留自绘标题栏才能继续显示 FPS 与图钉。
        var isMacOSSystem = isMacOS && _windowDecorationStyle == WindowDecorationStyle.System;
        ExtendClientAreaToDecorationsHint = useCustomButtons || isMacOSSystem;
        WindowDecorations = _windowDecorationStyle switch
        {
            WindowDecorationStyle.None => Avalonia.Controls.WindowDecorations.None,
            WindowDecorationStyle.System when !isMacOS => Avalonia.Controls.WindowDecorations.Full,
            _ when OperatingSystem.IsLinux() => Avalonia.Controls.WindowDecorations.BorderOnly,
            _ => Avalonia.Controls.WindowDecorations.Full
        };
        // macOS 风格自绘左侧四颗圆点，右侧只保留 FPS。
        WindowsTitleBarGroup.IsVisible = !useMacButtons;
        UpdateCaptionButtonsVisibility();
        // 顶栏自绘区域始终显示；Linux 系统装饰下会与系统标题栏同时存在。
        RootGrid.RowDefinitions[0].Height = new GridLength(36);
        TitleBarSurface.IsVisible = true;
        TitleBarLayout.IsVisible = true;
        UpdateBaseColorLayer();
        ApplyPlatformWindowControls();
    }

    // 系统全屏时 macOS 会自己显示窗口控件，自绘按钮不再渲染；
    // Windows/Linux 全屏没有系统控件，保留自绘按钮以便退出全屏。
    private void UpdateCaptionButtonsVisibility()
    {
        var isMacOSFullScreen = OperatingSystem.IsMacOS() && WindowState == WindowState.FullScreen;
        MacCaptionButtons.IsVisible = _windowDecorationStyle == WindowDecorationStyle.MacOS && !isMacOSFullScreen;
        CaptionButtons.IsVisible = _windowDecorationStyle == WindowDecorationStyle.Windows && !isMacOSFullScreen;
    }

    // Linux 合成器不保证模糊；macOS 全屏后透明/玻璃效果会失效。
    // 两种情况都垫一层跟随明暗主题的不透明底色，避免窗口透出桌面或发黑。
    private void UpdateBaseColorLayer()
    {
        BaseColorLayer.IsVisible = OperatingSystem.IsLinux()
                                   || (OperatingSystem.IsMacOS() && WindowState == WindowState.FullScreen);
    }

    // macOS 系统红绿灯只在系统装饰模式保留，自绘或无边框时隐藏；
    // 系统全屏时改由系统标题栏提供控件，这时恢复显示、并把自绘按钮让出去。
    private void ApplyPlatformWindowControls()
    {
        if (OperatingSystem.IsMacOS())
        {
            var hideNativeButtons = _windowDecorationStyle != WindowDecorationStyle.System
                                    && WindowState != WindowState.FullScreen;
            _macOSWindowControls.SetHidden(this, hideNativeButtons);
        }
    }

    // 进出系统全屏时 AppKit 会重建标题栏：原先隐藏的原生红绿灯、标题栏材质会重新出现，
    // 因此在状态切换时补一次，并在全屏过渡动画结束后再补一次。
    private void ReapplyNativeWindowChromeForFullScreen()
    {
        var isFullScreen = WindowState == WindowState.FullScreen;
        if (!isFullScreen && !_wasFullScreen)
        {
            return;
        }

        _wasFullScreen = isFullScreen;
        ApplyNativeWindowChrome();
        DispatcherTimer.RunOnce(
            ApplyNativeWindowChrome,
            TimeSpan.FromMilliseconds(700),
            DispatcherPriority.Background);
    }

    private void ApplyNativeWindowChrome()
    {
        ApplyPlatformWindowControls();
        _windowAppearanceService.ReapplyWindowEffect();
    }

    // 自绘圆点跟随窗口激活状态：失焦时转为灰色，回到前台恢复彩色。
    // 事件回调按事件语义直接切换，不读取 IsActive：激活事件可能早于属性更新。
    private void OnWindowActivated(object? sender, EventArgs args) => SetWindowActiveVisuals(true);

    private void OnWindowDeactivated(object? sender, EventArgs args) => SetWindowActiveVisuals(false);

    private void UpdateWindowActiveVisuals() => SetWindowActiveVisuals(IsActive);

    private void SetWindowActiveVisuals(bool isActive)
    {
        Classes.Set("window-inactive", !isActive);
    }

    // Wayland 部分合成器不提供系统标题栏时回退到 Windows 风格，避免出现无按钮窗口。
    private void ApplySystemDecorationFallback()
    {
        var platform = PlatformImpl;
        if (_windowDecorationStyle != WindowDecorationStyle.System
            || !OperatingSystem.IsLinux()
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || platform is null
            || !RequiresManagedDecorations(platform))
        {
            return;
        }

        AppLogger.Warning("System window decorations are unavailable; falling back to Windows style.");
        _windowDecorationStyle = WindowDecorationStyle.Windows;
        ApplyWindowDecorations();
    }

    // 引用程序集将 NeedsManagedDecorations 标为 internal，运行时库中为公开接口成员。
    private static bool RequiresManagedDecorations(Avalonia.Platform.IWindowImpl platform)
    {
        var property = typeof(Avalonia.Platform.IWindowImpl).GetProperty(
            "NeedsManagedDecorations",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return property?.GetValue(platform) is true;
    }
#if DEBUG
    private void AttachDevBadge()
    {
        var devBadge = new Border
        {
            Classes = { "debug-badge" },
            Child = new TextBlock
            {
                Classes = { "debug-badge-text" },
                Text = "Dev",
            }
        };
        AutomationProperties.SetAutomationId(devBadge, "TitleBar.DevBadge");
        TitleBarLayout.Children.Insert(0, devBadge);
    }
#endif

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        DetachViewModel();

        if (DataContext is MainWindowViewModel viewModel)
        {
            _attachedViewModel = viewModel;
            _attachedViewModel.Theme.CustomAccentRequested += OnCustomAccentRequested;
            _attachedViewModel.Theme.WindowDecorationRestartRequested += OnWindowDecorationRestartRequested;
            _attachedViewModel.PropertyChanged += OnAttachedViewModelPropertyChanged;
            _windowAppearanceService.Attach(this, viewModel.Theme);
            if (_hasOpened)
            {
                _systemAccentColorService.Attach(this, viewModel.Theme);
            }
            InitializePageHosts();
            AttachNavIndicator(viewModel);
        }
    }

    private void AttachNavIndicator(MainWindowViewModel viewModel)
    {
        _navIndicatorTarget = ResolveNavButton(viewModel.CurrentPage);
        _navIndicatorRect = null;
        SidebarLayout.LayoutUpdated += OnSidebarLayoutUpdated;
        NavSelectionIndicator.PropertyChanged += OnNavIndicatorPropertyChanged;
        foreach (var (_, button) in NavButtons())
        {
            button.AddHandler(
                Control.RequestBringIntoViewEvent,
                OnNavBringIntoViewRequest,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
        }
        UpdateNavIndicator(animate: false);
        // 首屏没有位移动画，选中态与图标形状直接生效。
        _navVisualTimer?.Stop();
        _navVisualTimer = null;
        ApplyNavIconMorph(viewModel.CurrentPage);
        ApplyNavSelectionVisuals(viewModel.CurrentPage);
    }

    private void DetachNavIndicator()
    {
        SidebarLayout.LayoutUpdated -= OnSidebarLayoutUpdated;
        NavSelectionIndicator.PropertyChanged -= OnNavIndicatorPropertyChanged;
        foreach (var (_, button) in NavButtons())
        {
            button.RemoveHandler(Control.RequestBringIntoViewEvent, OnNavBringIntoViewRequest);
        }
        _navVisualTimer?.Stop();
        _navVisualTimer = null;
        _navScrollTimer?.Stop();
        _navScrollTimer = null;
        _navIndicatorTarget = null;
        _navIndicatorRect = null;
    }

    // 一次切换的三段时序：60ms 旧项回落线条图标，100ms 新项开始配色渐变，160ms 切换图标形状。
    private void StartNavVisualTimeline()
    {
        _navVisualTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var startedAt = Stopwatch.GetTimestamp();
        var unmorphed = false;
        var selected = false;
        timer.Tick += (_, _) =>
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (!unmorphed && elapsed >= NavIconUnmorphDelay)
            {
                unmorphed = true;
                ApplyNavIconMorph(null);
            }

            if (!selected && elapsed >= NavSelectionRevealDelay)
            {
                selected = true;
                ApplyNavSelectionVisuals(_attachedViewModel?.CurrentPage);
            }

            if (elapsed < NavIconMorphDelay)
            {
                return;
            }

            timer.Stop();
            if (ReferenceEquals(_navVisualTimer, timer))
            {
                _navVisualTimer = null;
            }

            ApplyNavIconMorph(_attachedViewModel?.CurrentPage);
        };
        _navVisualTimer = timer;
        timer.Start();
    }

    private void ApplyNavSelectionVisuals(AppNavigationPage? page) => SetNavItemClass("selected", page);

    // 旧项需要立刻回到普通色：临时关闭颜色过渡，撤掉选中类后再恢复。
    private void RevertNavSelectionImmediately()
    {
        foreach (var (_, button) in NavButtons())
        {
            if (!button.Classes.Contains("selected"))
            {
                continue;
            }

            SuppressNavColorTransitions(button, suppress: true);
            button.Classes.Remove("selected");
            Dispatcher.UIThread.Post(
                () => SuppressNavColorTransitions(button, suppress: false),
                DispatcherPriority.Background);
        }
    }

    private static void SuppressNavColorTransitions(Button button, bool suppress)
    {
        SetTransitions(button);
        foreach (var icon in button.GetVisualDescendants().OfType<PackIconMingCuteIcons>())
        {
            SetTransitions(icon);
        }

        void SetTransitions(Animatable element)
        {
            if (suppress)
            {
                element.Transitions = null;
            }
            else
            {
                element.ClearValue(StyledElement.TransitionsProperty);
            }
        }
    }

    // 图标形状（线条 ↔ 实心）跟随点击立即切换，配色仍走延迟渐变。
    private void ApplyNavIconMorph(AppNavigationPage? page) => SetNavItemClass("nav-icon-morph", page);

    private void SetNavItemClass(string className, AppNavigationPage? page)
    {
        foreach (var (navPage, button) in NavButtons())
        {
            button.Classes.Set(className, navPage == page);
        }
    }

    private IEnumerable<(AppNavigationPage Page, Button Button)> NavButtons()
    {
        yield return (AppNavigationPage.Home, NavHomeButton);
        yield return (AppNavigationPage.Proxy, NavProxyButton);
        yield return (AppNavigationPage.Connections, NavConnectionsButton);
        yield return (AppNavigationPage.CoreLogs, NavCoreLogsButton);
        yield return (AppNavigationPage.Rules, NavRulesButton);
        yield return (AppNavigationPage.Subscriptions, NavSubscriptionsButton);
        yield return (AppNavigationPage.Overrides, NavOverridesButton);
        yield return (AppNavigationPage.Settings, NavSettingsButton);
    }

    private Button? ResolveNavButton(AppNavigationPage page) =>
        NavButtons().Where(entry => entry.Page == page).Select(entry => entry.Button).FirstOrDefault();

    // 布局变化（侧栏收放、窗口缩放、滚动）时让底板贴住目标，不播放位移动画。
    private void OnSidebarLayoutUpdated(object? sender, EventArgs args)
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        var target = _navIndicatorTarget ?? ResolveNavButton(_attachedViewModel.CurrentPage);
        if (target is null)
        {
            return;
        }

        _navIndicatorTarget = target;
        UpdateNavIndicator(animate: false);
        RefreshNavIndicatorMask();
    }

    private void UpdateNavIndicator(bool animate)
    {
        var target = _navIndicatorTarget;
        if (target is null)
        {
            return;
        }

        var origin = target.TranslatePoint(new Point(0, 0), SidebarLayout);
        var size = target.Bounds.Size;
        if (origin is not { } point || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        // 目标没动就不重复写入，避免打断正在进行中的位移过渡。
        if (!animate
            && _navIndicatorRect is { } current
            && Math.Abs(current.X - point.X) < 0.5
            && Math.Abs(current.Y - point.Y) < 0.5
            && Math.Abs(current.Width - size.Width) < 0.5
            && Math.Abs(current.Height - size.Height) < 0.5)
        {
            return;
        }

        ApplyNavIndicatorRect(point, size, animate);
    }

    private void ApplyNavIndicatorRect(Point origin, Size size, bool animate)
    {
        var isFirstPlacement = _navIndicatorRect is null;
        _navIndicatorRect = new Rect(origin, size);

        var transitions = NavSelectionIndicator.Transitions;
        if (!animate || isFirstPlacement)
        {
            NavSelectionIndicator.Transitions = null;
        }

        NavSelectionIndicator.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        NavSelectionIndicator.Width = size.Width;
        NavSelectionIndicator.Height = size.Height;
        // 可见性完全由边缘遮罩决定，底板自身保持不透明。
        NavSelectionIndicator.Opacity = 1;
        RefreshNavIndicatorMask();

        if (!animate || isFirstPlacement)
        {
            NavSelectionIndicator.Transitions = transitions;
        }
    }

    // 把目标项滚进视口，返回内容需要上移的像素数（0 表示无需滚动）。
    // 「设置」固定在滚动区外，不应触发导航列表滚动。
    private double GetNavScrollDelta(Button target)
    {
        var scroll = SidebarNavigationScroll;
        if (scroll.Viewport.Height <= 0 || !target.GetVisualAncestors().Contains(scroll))
        {
            return 0;
        }

        var origin = target.TranslatePoint(new Point(0, 0), scroll);
        return origin is { } point ? ComputeNavScrollDelta(point.Y, target.Bounds.Height) : 0;
    }

    private double ComputeNavScrollDelta(double itemTop, double itemHeight)
    {
        var scroll = SidebarNavigationScroll;
        var viewportHeight = scroll.Viewport.Height;
        if (viewportHeight <= 0)
        {
            return 0;
        }

        const double padding = 6;
        var desired = scroll.Offset.Y;
        if (itemTop < padding)
        {
            desired = scroll.Offset.Y + itemTop - padding;
        }
        else if (itemTop + itemHeight > viewportHeight - padding)
        {
            desired = scroll.Offset.Y + itemTop + itemHeight - viewportHeight + padding;
        }

        var maxOffset = Math.Max(0, scroll.Extent.Height - viewportHeight);
        desired = Math.Clamp(desired, 0, maxOffset);
        var delta = desired - scroll.Offset.Y;
        return Math.Abs(delta) < 0.5 ? 0 : delta;
    }

    // 焦点变化时 Avalonia 会瞬间 BringIntoView；这里接管为缓动滚动。
    private void OnNavBringIntoViewRequest(object? sender, RequestBringIntoViewEventArgs args)
    {
        args.Handled = true;
        if (args.TargetObject is not Visual target)
        {
            return;
        }

        var origin = target.TranslatePoint(args.TargetRect.TopLeft, SidebarNavigationScroll);
        if (origin is not { } point)
        {
            return;
        }

        var delta = ComputeNavScrollDelta(point.Y, args.TargetRect.Height);
        if (Math.Abs(delta) >= 0.5 && _navScrollTimer is null)
        {
            StartNavScrollAnimation(delta);
        }
    }

    // 用定时器推进 Offset：每帧都会触发 ScrollChanged，底板随之逐帧跟随目标。
    private void StartNavScrollAnimation(double delta)
    {
        _navScrollTimer?.Stop();
        var scroll = SidebarNavigationScroll;
        var from = scroll.Offset.Y;
        var to = from + delta;
        var duration = NavScrollAnimationDuration.TotalMilliseconds;
        var startedAt = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds / duration,
                0,
                1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            scroll.Offset = new Vector(scroll.Offset.X, from + (to - from) * eased);
            if (progress < 1)
            {
                return;
            }

            timer.Stop();
            if (ReferenceEquals(_navScrollTimer, timer))
            {
                _navScrollTimer = null;
            }

            scroll.Offset = new Vector(scroll.Offset.X, to);
            UpdateNavIndicator(animate: false);
        };
        _navScrollTimer = timer;
        timer.Start();
    }

    // 位移动画期间逐帧按当前矩形重算遮罩，滚动区外的部分才能真正隐去。
    private void RefreshNavIndicatorMask()
    {
        var target = _navIndicatorTarget;
        var height = NavSelectionIndicator.Height;
        if (target is null || height <= 0)
        {
            NavSelectionIndicator.OpacityMask = null;
            return;
        }

        ApplyNavIndicatorEdgeFade(target, NavSelectionIndicator.Margin.Top, height);
    }

    private void OnNavIndicatorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Layoutable.MarginProperty
            || args.Property == Layoutable.HeightProperty
            || args.Property == Layoutable.WidthProperty)
        {
            RefreshNavIndicatorMask();
        }
    }

    // 底板保持完整尺寸与圆角，只把视口外的部分用遮罩隐去，避免圆角被裁成收缩状。
    private void ApplyNavIndicatorEdgeFade(Button target, double top, double height)
    {
        if (height <= 0 || !target.GetVisualAncestors().Contains(SidebarNavigationScroll))
        {
            NavSelectionIndicator.OpacityMask = null;
            return;
        }

        var viewportHeight = SidebarNavigationScroll.Bounds.Height;
        if (viewportHeight <= 0)
        {
            NavSelectionIndicator.OpacityMask = null;
            return;
        }

        var hasContentAbove = SidebarNavigationScroll.Offset.Y > 0;
        var hasContentBelow = SidebarNavigationScroll.Extent.Height
                              - SidebarNavigationScroll.Offset.Y
                              - viewportHeight > 0.5;
        if (!hasContentAbove && !hasContentBelow)
        {
            NavSelectionIndicator.OpacityMask = null;
            return;
        }

        var fadePixels = Math.Min(viewportHeight / 2, SidebarEdgeFadeHeight);
        if (fadePixels <= 0)
        {
            NavSelectionIndicator.OpacityMask = null;
            return;
        }

        // 视口上下边界换算到指示器自身坐标系；视口外始终透明。
        var viewportTop = (SidebarNavigationScroll.TranslatePoint(new Point(0, 0), SidebarLayout)?.Y ?? 0) - top;
        var viewportBottom = viewportTop + viewportHeight;

        double AlphaAt(double y)
        {
            if (y < viewportTop - 0.01 || y > viewportBottom + 0.01)
            {
                return 0;
            }

            var fromTop = hasContentAbove ? Math.Clamp((y - viewportTop) / fadePixels, 0, 1) : 1;
            var fromBottom = hasContentBelow ? Math.Clamp((viewportBottom - y) / fadePixels, 0, 1) : 1;
            return Math.Min(fromTop, fromBottom);
        }

        var breakpoints = new SortedSet<double> { 0, height };
        foreach (var pixel in new[] { viewportTop, viewportTop + fadePixels, viewportBottom - fadePixels, viewportBottom })
        {
            if (pixel > 0 && pixel < height)
            {
                breakpoints.Add(pixel);
            }
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, height, RelativeUnit.Absolute),
        };
        foreach (var pixel in breakpoints)
        {
            var offset = Math.Clamp(pixel / height, 0, 1);
            if (brush.GradientStops.Count > 0
                && Math.Abs(brush.GradientStops[brush.GradientStops.Count - 1].Offset - offset) < 1e-6)
            {
                continue;
            }

            brush.GradientStops.Add(new GradientStop(MaskColor(AlphaAt(pixel)), offset));
        }

        NavSelectionIndicator.OpacityMask = brush;
    }

    private static Color MaskColor(double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 0, 0, 0);

    private void DetachViewModel()
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        _attachedViewModel.Theme.CustomAccentRequested -= OnCustomAccentRequested;
        _attachedViewModel.Theme.WindowDecorationRestartRequested -= OnWindowDecorationRestartRequested;
        _attachedViewModel.PropertyChanged -= OnAttachedViewModelPropertyChanged;
        DetachNavIndicator();
        _windowAppearanceService.Dispose();
        _systemAccentColorService.Dispose();
        _attachedViewModel = null;
    }

    private void OnOpened(object? sender, EventArgs args)
    {
#if DEBUG
        var openedAt = Stopwatch.GetTimestamp();
        AppLogger.Info("[StartupTrace] Main window opened");
        Dispatcher.UIThread.Post(
            () => AppLogger.Info($"[StartupTrace] Main window first background turn elapsed={Stopwatch.GetElapsedTime(openedAt).TotalMilliseconds:0.0}ms"),
            DispatcherPriority.Background);
#endif
        _hasOpened = true;
        _relativeTimeTimer.Start();
        ApplyPlatformWindowControls();
        ApplySystemDecorationFallback();
        if (DataContext is MainWindowViewModel viewModel)
        {
            _systemAccentColorService.Attach(this, viewModel.Theme);
            if (_visiblePageHost is null)
            {
                ShowInitialPage(viewModel.CurrentPage);
            }
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        // macOS：⌘Q 与关闭按钮一致（按设置隐藏到托盘或退出）；真正的退出走应用菜单「Quit」/托盘菜单。
        if (!args.Handled
            && OperatingSystem.IsMacOS()
            && args.Key == Key.Q
            && args.KeyModifiers == KeyModifiers.Meta)
        {
            Close();
            args.Handled = true;
            return;
        }

        if (args.Handled
            || args.Key != Key.Escape
            || args.KeyModifiers != KeyModifiers.None
            || DialogHost.IsOpen
            || ConnectivityDialogHost.IsOpen
            || UpdateDialogHost.IsOpen
            || RestartRequiredDialogHost.IsOpen
            || DataContext is not MainWindowViewModel viewModel
            || viewModel.SubscriptionPage.Provider.IsProviderSelectorDialogVisible
            || viewModel.CurrentPage != AppNavigationPage.Settings
            || !viewModel.Settings.IsBackVisible)
        {
            return;
        }

        if (viewModel.Settings.BackCommand.CanExecute(null))
        {
            viewModel.Settings.BackCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void OnAttachedViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (args.PropertyName == nameof(MainWindowViewModel.IsCurrentPageLoading))
        {
            if (!IsVisible) return;
            if (_pendingPageHost is { } pending)
            {
                if (pending.Content is not null)
                {
                    CompletePageLoadingThenEnter(_visiblePageHost, pending, _pageTransitionVersion);
                }
            }
            else if (viewModel.IsCurrentPageLoading && _visiblePageHost is not null)
            {
                AnimatePageTransition(viewModel.CurrentPage);
            }
            return;
        }
        if (args.PropertyName != nameof(MainWindowViewModel.CurrentPage)) return;

        AnimatePageTransition(viewModel.CurrentPage);
        MoveNavIndicator(viewModel.CurrentPage);
#if DEBUG
        ScheduleNavigationDebugLog(viewModel.CurrentPage);
#endif
    }

    private void MoveNavIndicator(AppNavigationPage page)
    {
        var target = ResolveNavButton(page);
        if (target is null || ReferenceEquals(target, _navIndicatorTarget))
        {
            return;
        }

        _navIndicatorTarget = target;
        // 旧项立刻回到普通色并在 60ms 回落到线条图标；新项 100ms 后渐变选中色，160ms 切换形状。
        RevertNavSelectionImmediately();

        // 需要滚动时先贴住目标当前位置，随后随滚动逐帧跟入；否则直接平滑滑过去。
        var isScrolling = _navScrollTimer is not null;
        var scrollDelta = GetNavScrollDelta(target);
        var origin = target.TranslatePoint(new Point(0, 0), SidebarLayout);
        var size = target.Bounds.Size;
        if (origin is { } point && size.Width > 0 && size.Height > 0)
        {
            ApplyNavIndicatorRect(point, size, animate: !isScrolling && Math.Abs(scrollDelta) < 0.5);
        }

        if (!isScrolling && Math.Abs(scrollDelta) >= 0.5)
        {
            StartNavScrollAnimation(scrollDelta);
        }
        StartNavVisualTimeline();
    }

    // 将页面映射到持久宿主；XAML 按导航顺序堆叠宿主。
    private void InitializePageHosts()
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        if (!_pageHostsReady)
        {
            _pageHosts[AppNavigationPage.Home] = HomePageHost;
            _pageHosts[AppNavigationPage.Proxy] = ProxyPageHost;
            _pageHosts[AppNavigationPage.Connections] = ConnectionsPageHost;
            _pageHosts[AppNavigationPage.CoreLogs] = CoreLogsPageHost;
            _pageHosts[AppNavigationPage.Rules] = RulesPageHost;
            _pageHosts[AppNavigationPage.Subscriptions] = SubscriptionsPageHost;
            _pageHosts[AppNavigationPage.Overrides] = OverridesPageHost;
            _pageHosts[AppNavigationPage.Settings] = SettingsPageHost;
            // 代理页作为单层纹理参与整页变换，避免动画期间重复绘制大视觉树。
            ProxyPageHost.CacheMode = _proxyPageBitmapCache;
            _pageHostsReady = true;
        }

        if (IsVisible)
        {
            ShowInitialPage(_attachedViewModel.CurrentPage);
        }
    }

    private void EnsurePageLoaded(AppNavigationPage page)
    {
        if (!_pageHostsReady
            || !_pageHosts.TryGetValue(page, out var host)
            || host.Content is not null
            || !TryGetPageConverter(out var converter))
        {
            return;
        }

        host.Content = converter.GetOrCreateView(page);
    }

    private void SetPageLoadingVisible(bool isVisible)
    {
        if (isVisible)
        {
            if (_pageLoadingShownAt != 0) return;
            _pageLoadingShownAt = Stopwatch.GetTimestamp();
            PageLoadingIndicator.Start();
            PageLoadingOverlay.Opacity = 1;
            PageLoadingOverlay.IsHitTestVisible = true;
            return;
        }

        PageLoadingOverlay.Opacity = 0;
        PageLoadingOverlay.IsHitTestVisible = false;
        PageLoadingIndicator.Stop();
        _pageLoadingShownAt = 0;
    }

    // 首页直接显示无动画；其余宿主复位到隐藏的下浮起始态。
    private void ShowInitialPage(AppNavigationPage page)
    {
        if (!_pageHosts.TryGetValue(page, out var host))
        {
            return;
        }

        CancelPendingPageTransition();
        foreach (var other in _pageHosts.Values)
        {
            DeactivatePageHost(other);
            other.Transitions = null;
            other.ZIndex = 0;
            other.IsHitTestVisible = false;
            other.Opacity = 0;
            other.RenderTransform = PageTransition.EnterFromTransform;
            // 非当前页退出布局，避免多棵视觉树持续参与 Measure。
            other.IsVisible = false;
        }

        _visiblePageHost = null;
        if (_attachedViewModel?.IsCurrentPageLoading == true)
        {
            AnimatePageTransition(page);
            return;
        }

        EnsurePageLoaded(page);
        host.IsVisible = true;
        ShowPageHost(host);
        _visiblePageHost = host;
        ActivatePageHost(host);
        PreparePageLayout(page, host);
    }

    private void AnimatePageTransition(AppNavigationPage page)
    {
        if (!IsVisible || !_pageHostsReady || !_pageHosts.TryGetValue(page, out var nextHost))
        {
            return;
        }

        var isDataLoading = _attachedViewModel?.IsCurrentPageLoading == true;
        if (ReferenceEquals(nextHost, _visiblePageHost) && nextHost.Content is not null && !isDataLoading)
        {
            CancelPendingPageTransition();
            ShowPageHost(nextHost);
            ActivatePageHost(nextHost);
            return;
        }

        CancelPendingPageTransition();
        var previousHost = _visiblePageHost;
        var version = ++_pageTransitionVersion;
        _pendingPageHost = nextHost;
        if (previousHost is not null)
        {
            previousHost.IsHitTestVisible = false;
        }

        if (nextHost.Content is not null && !isDataLoading)
        {
            // 缓存页已有视觉树，直接启动过渡，避免等待空闲合成器唤醒。
            PrepareNextHostEnterState(nextHost);
            ActivatePageHost(nextHost);
            StartPageTransition(previousHost, nextHost, version);
            return;
        }

        // 先让加载状态进入渲染队列，再在后台优先级创建页面。
        if (previousHost is not null)
        {
            DeactivatePageHost(previousHost);
            previousHost.Transitions = null;
            previousHost.Opacity = 0;
            previousHost.IsVisible = false;
            previousHost.IsHitTestVisible = false;
            previousHost.ZIndex = 0;
        }

        SetPageLoadingVisible(true);
        if (nextHost.Content is not null)
        {
            // 缓存页立即准备显示状态，避免数据就绪通知先于后台布局回调。
            PrepareNextHostEnterState(nextHost);
            ActivatePageHost(nextHost);
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (version != _pageTransitionVersion || !ReferenceEquals(_pendingPageHost, nextHost))
                {
                    return;
                }

                EnsurePageLoaded(page);
                PrepareNextHostEnterState(nextHost);
                ActivatePageHost(nextHost);
                CompletePageLoadingThenEnter(previousHost, nextHost, version);
            },
            DispatcherPriority.Background);
    }

    private static void PrepareNextHostEnterState(ContentControl nextHost)
    {
        nextHost.IsVisible = true;
        nextHost.Transitions = null;
        nextHost.ZIndex = 1;
        nextHost.IsHitTestVisible = false;
        nextHost.Opacity = 0;
        nextHost.RenderTransform = PageTransition.EnterFromTransform;
    }

    private void CompletePageLoadingThenEnter(
        ContentControl? previousHost,
        ContentControl nextHost,
        long version)
    {
        if (version != _pageTransitionVersion || !ReferenceEquals(_pendingPageHost, nextHost))
        {
            return;
        }

        // 视觉树创建与业务加载分别就绪，数据未返回时保持遮罩。
        if (_attachedViewModel?.IsCurrentPageLoading == true) return;

        var remaining = PageLoadingMinVisible - Stopwatch.GetElapsedTime(_pageLoadingShownAt);
        if (remaining <= TimeSpan.Zero)
        {
            StartPageTransition(previousHost, nextHost, version);
            return;
        }

        var timer = new DispatcherTimer { Interval = remaining };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CompletePageLoadingThenEnter(previousHost, nextHost, version);
        };
        timer.Start();
    }

    private void StartPageTransition(ContentControl? previousHost, ContentControl nextHost, long version)
    {
        if (version != _pageTransitionVersion || !ReferenceEquals(_pendingPageHost, nextHost))
        {
            return;
        }

        PreparePageLayout(_attachedViewModel!.CurrentPage, nextHost);
        SetPageLoadingVisible(false);
        _pendingPageHost = null;
        _visiblePageHost = nextHost;
        if (previousHost is not null
            && !ReferenceEquals(previousHost, nextHost)
            && previousHost.IsVisible)
        {
            DeactivatePageHost(previousHost);
            previousHost.Transitions = PageTransition.CreateLeaveTransitions();
            previousHost.ZIndex = 0;
            previousHost.IsHitTestVisible = false;
            previousHost.Opacity = 0;
            previousHost.RenderTransform = PageTransition.LeaveToTransform;
            ScheduleHidePageHost(previousHost);
        }
        else if (previousHost is not null && !ReferenceEquals(previousHost, nextHost))
        {
            HidePageHost(previousHost);
        }

        nextHost.Transitions = PageTransition.CreateEnterTransitions();
        nextHost.IsHitTestVisible = true;
        nextHost.Opacity = 1;
        nextHost.RenderTransform = PageTransition.RestTransform;
    }

    private void CancelPendingPageTransition()
    {
        _pageTransitionVersion++;
        SetPageLoadingVisible(false);
        if (_pendingPageHost is not { } pendingHost)
        {
            return;
        }

        DeactivatePageHost(pendingHost);
        HidePageHost(pendingHost);
        _pendingPageHost = null;
    }

    // 离场动画结束后再踢出布局，避免快切时误藏当前页。
    private void ScheduleHidePageHost(ContentControl host)
    {
        var timer = new DispatcherTimer { Interval = PageTransition.LeaveDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(host, _visiblePageHost)
                || ReferenceEquals(host, _pendingPageHost))
            {
                return;
            }

            HidePageHost(host);
        };
        timer.Start();
    }

    private static void HidePageHost(ContentControl host)
    {
        host.Transitions = null;
        host.ZIndex = 0;
        host.IsHitTestVisible = false;
        host.Opacity = 0;
        host.RenderTransform = PageTransition.EnterFromTransform;
        host.IsVisible = false;
    }

    private static void ShowPageHost(ContentControl host)
    {
        host.Transitions = null;
        host.ZIndex = 1;
        host.IsHitTestVisible = true;
        host.Opacity = 1;
        host.RenderTransform = PageTransition.RestTransform;
        host.IsVisible = true;
    }

    private static void ActivatePageHost(ContentControl host)
    {
        if (host.Content is IPageContentLifecycle lifecycle)
        {
            lifecycle.ActivatePageContent();
        }
    }

    private static void DeactivatePageHost(ContentControl? host)
    {
        if (host?.Content is IPageContentLifecycle lifecycle)
        {
            lifecycle.DeactivatePageContent();
        }
    }

    private bool TryGetPageConverter(out PageToViewConverter converter)
    {
        if (TryGetResource("PageToView", ActualThemeVariant, out var resource)
            && resource is PageToViewConverter pageToView)
        {
            converter = pageToView;
            return true;
        }

        converter = null!;
        return false;
    }

#if DEBUG
    internal object CapturePageLoadingState()
    {
        var viewModel = _attachedViewModel!;
        var host = _pageHosts[viewModel.CurrentPage];
        return new
        {
            Page = viewModel.CurrentPage.ToString(),
            IsDataLoading = viewModel.IsCurrentPageLoading,
            IsOverlayVisible = IsVisible && PageLoadingOverlay.Opacity > 0,
            IsContentVisible = IsVisible && host.IsVisible && host.Opacity > 0,
            Pages = Enum.GetValues<AppNavigationPage>().ToDictionary(
                page => page.ToString(), page => viewModel.GetPageLoadingState(page)?.IsLoading == true),
        };
    }

    internal Task WaitForPageReadyAsync(AppNavigationPage page)
    {
        if (!_pageHosts.TryGetValue(page, out var host))
        {
            return Task.FromException(new InvalidOperationException($"Page host is not available: {page}"));
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WaitForPageReadyOnNextFrame(page, host, completion);
        return completion.Task;
    }

    // 数据就绪且页面开始显示后才响应，避免查询抢占首次加载或同页刷新。
    private void WaitForPageReadyOnNextFrame(
        AppNavigationPage page,
        ContentControl host,
        TaskCompletionSource completion)
    {
        RequestAnimationFrame(
            _ =>
            {
                if (_attachedViewModel?.CurrentPage != page)
                {
                    completion.TrySetException(new InvalidOperationException($"Navigation was superseded: {page}"));
                    return;
                }

                if (ReferenceEquals(_visiblePageHost, host)
                    && _pendingPageHost is null
                    && !_attachedViewModel.IsCurrentPageLoading
                    && host.Content is Control
                    && host.IsVisible
                    && host.Opacity > 0)
                {
                    completion.TrySetResult();
                    return;
                }

                WaitForPageReadyOnNextFrame(page, host, completion);
            });
    }

    [AvaloniaHotReload]
    private void OnHotReloaded()
    {
        ScheduleHotReloadRecovery();
    }

    private void ScheduleHotReloadRecovery()
    {
        var version = System.Threading.Interlocked.Increment(ref _hotReloadRecoveryVersion);
        Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(
                () => ApplyHotReloadRecovery(version),
                DispatcherPriority.Background),
            DispatcherPriority.Render);
    }

    private void ApplyHotReloadRecovery(long version)
    {
        if (version != System.Threading.Volatile.Read(ref _hotReloadRecoveryVersion))
        {
            return;
        }

        try
        {
            _windowAppearanceService.Reapply();
            _systemAccentColorService.Reapply();
            RebuildNavigationViewsAfterHotReload();
            AppLogger.Info("Hot reload recovered theme, accent color, and page view cache");
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, "Hot reload recovery failed");
        }
    }

    private void RebuildNavigationViewsAfterHotReload()
    {
        if (_attachedViewModel is null || !TryGetPageConverter(out var converter))
        {
            return;
        }

        // 热重载会替换视觉树和资源。
        // 宿主与缓存视图必须重新绑定，避免继续引用旧资源。
        ClearPageHostContents();
        converter.ClearCache();
        SetPageLoadingVisible(false);
        CancelPendingPageTransition();
        _pageHostsReady = false;
        _pageHosts.Clear();
        _visiblePageHost = null;
        InitializePageHosts();
    }

    private void ScheduleNavigationDebugLog(AppNavigationPage page)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var version = ++_navigationDebugVersion;
        Dispatcher.UIThread.Post(
            () => Dispatcher.UIThread.Post(
                () => LogNavigationDebug(page, startedAt, version),
                DispatcherPriority.Background),
            DispatcherPriority.Render);
    }

    private void LogNavigationDebug(AppNavigationPage page, long startedAt, long version)
    {
        if (version != _navigationDebugVersion
            || _attachedViewModel is null
            || _attachedViewModel.CurrentPage != page)
        {
            return;
        }

        // 首次创建走 Background，可能比本日志队列更晚完成。
        if (GetNavigationPageView(page) is null && PageLoadingOverlay.Opacity > 0)
        {
            Dispatcher.UIThread.Post(
                () => LogNavigationDebug(page, startedAt, version),
                DispatcherPriority.Background);
            return;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var controls = CountPageControls(GetNavigationPageView(page));
        AppLogger.Info($"Navigation complete: page={FormatPageDebugName(page)} elapsed={elapsedMs:0.0}ms controls={controls.Total} visible={controls.Visible} automation={controls.Automation} {BuildPageDebugState(page, _attachedViewModel)}");
    }

    private static (int Total, int Visible, int Automation) CountPageControls(Control? page)
    {
        if (page is not null)
        {
            var controls = page.GetVisualDescendants().OfType<Control>().Append(page).ToList();
            return (
                controls.Count,
                controls.Count(IsControlEffectivelyVisible),
                controls.Count(control => !string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(control))));
        }

        return (0, 0, 0);
    }

    private Control? GetNavigationPageView(AppNavigationPage page)
        => _pageHosts.TryGetValue(page, out var host) ? host.Content as Control : null;

    private static string BuildPageDebugState(AppNavigationPage page, MainWindowViewModel viewModel)
    {
        return page switch
        {
            AppNavigationPage.Proxy => $"groups={viewModel.ProxyPage.VisibleGroups.Count} nodes={viewModel.ProxyPage.VisibleNodeRows.Count} realized={viewModel.ProxyPage.RealizedNodeRowCount} parsed_nodes={viewModel.ProxyPage.ParsedNodeCount ?? 0}",
            AppNavigationPage.Connections => $"connections={viewModel.ConnectionPage.TotalConnectionCount} filtered={viewModel.ConnectionPage.FilteredConnectionCount}",
            AppNavigationPage.CoreLogs => $"core_logs={viewModel.CoreLogPage.TotalLogCount} filtered={viewModel.CoreLogPage.FilteredLogCount}",
            AppNavigationPage.Rules => $"rules={viewModel.RulePage.Rules.Count} filtered={viewModel.RulePage.FilteredRules.Count}",
            AppNavigationPage.Subscriptions => $"subscriptions={viewModel.SubscriptionPage.TotalSubscriptionCount} current={viewModel.SubscriptionPage.CurrentSubscriptionId ?? string.Empty}",
            AppNavigationPage.Overrides => $"overrides={viewModel.OverridePage.Overrides.Count}",
            _ => string.Empty
        };
    }

    private static string FormatPageDebugName(AppNavigationPage page) => page.ToString();

    private static bool IsControlEffectivelyVisible(Control control)
    {
        for (var current = control; current is not null; current = current.GetVisualParent<Control>())
        {
            if (!current.IsVisible)
            {
                return false;
            }
        }

        return true;
    }
#endif

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (args.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        BeginMoveDrag(args);
    }

    private void OnSidebarScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        UpdateNavIndicator(animate: false);

        var viewportHeight = scrollViewer.Viewport.Height;
        var remainingHeight = scrollViewer.Extent.Height - scrollViewer.Offset.Y - viewportHeight;
        var hasContentAbove = scrollViewer.Offset.Y > 0;
        var hasContentBelow = remainingHeight > 0;
        if (viewportHeight <= 0 || (!hasContentAbove && !hasContentBelow))
        {
            scrollViewer.OpacityMask = null;
            return;
        }

        // 只处理导航滚动区域，底部固定的设置按钮不参与渐隐。
        var fadeOffset = Math.Min(0.5, SidebarEdgeFadeHeight / viewportHeight);
        var stops = _sidebarEdgeFadeMask.GradientStops;
        stops[0].Color = hasContentAbove ? Colors.Transparent : Colors.Black;
        stops[1].Offset = hasContentAbove ? fadeOffset : 0;
        stops[2].Offset = hasContentBelow ? 1 - fadeOffset : 1;
        stops[3].Color = hasContentBelow ? Colors.Transparent : Colors.Black;
        scrollViewer.OpacityMask = _sidebarEdgeFadeMask;
    }

    private void OnSidebarToggleClicked(object? sender, RoutedEventArgs args)
    {
        var isCollapsed = Classes.Contains("sidebar-collapsed");
        IsSidebarManuallyCollapsed = !isCollapsed;
        if (isCollapsed && CurrentSidebarWindowWidth < SidebarAutoCollapseWidth)
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }

            Width = SidebarAutoCollapseWidth;
        }

        UpdateSidebarState();
        _windowStateService.SaveNow();
    }

    private double CurrentSidebarWindowWidth => Bounds.Width > 0 ? Bounds.Width : Width;

    private void UpdateSidebarState()
    {
        // 窄窗口只临时折叠，持久化始终使用用户手动选择的状态。
        var isCollapsed = IsSidebarManuallyCollapsed || CurrentSidebarWindowWidth < SidebarAutoCollapseWidth;
        if (isCollapsed)
        {
            Classes.Add("sidebar-collapsed");
        }
        else
        {
            Classes.Remove("sidebar-collapsed");
        }
    }

    private void OnPinClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        Topmost = !Topmost;
        UpdatePinState();
    }

    private void OnWindowDecorationRestartRequested(object? sender, EventArgs args)
    {
        RestartRequiredDialogHost.IsOpen = true;
    }

    private void OnRestartRequiredLaterClicked(object? sender, RoutedEventArgs args)
    {
        RestartRequiredDialogHost.IsOpen = false;
    }

    private void OnRestartRequiredRestartClicked(object? sender, RoutedEventArgs args)
    {
        _ = RestartUiWindowCoreAsync();
    }

    private async Task RestartUiWindowCoreAsync()
    {
        RestartRequiredDialogHost.IsOpen = false;
        if (RestartUiWindowAsync is null)
        {
            return;
        }

        try
        {
            if (await RestartUiWindowAsync())
            {
                RequestUiShutdown();
                return;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Desktop UI restart failed: {exception.Message}");
        }

        RestartRequiredDialogHost.IsOpen = true;
    }

    private void UpdatePinState()
    {
        UpdatePinVisual(PinButton, PinOutlineIcon, PinFilledIcon);
        // macOS 风格图钉固定用实心图标，只区分“是否常显”。
        MacPinFilledIcon.IsVisible = true;
        MacPinButton.Classes.Set("active", Topmost);
    }

    private void UpdatePinVisual(Button button, Visual outlineIcon, Visual filledIcon)
    {
        filledIcon.IsVisible = Topmost;
        outlineIcon.IsVisible = !Topmost;
        button.Classes.Set("active", Topmost);
    }

    private void OnMinimizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        ToggleWindowMaximized();
    }

    // macOS 风格绿灯：语义对齐系统行为，切换系统全屏（原生 Space），而不是最大化。
    private void OnMacZoomClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
    }

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        Close();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowStateVisuals();
    }

    private void UpdateWindowStateVisuals()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !isMaximized;
        RestoreIcon.IsVisible = isMaximized;
    }

    public void RequestShutdown()
    {
        BeginShutdown(shouldShutdownTray: true);
    }

    internal void RequestUiShutdown()
    {
        BeginShutdown(shouldShutdownTray: false);
    }

    internal void HideToBackground()
    {
        if (_isShutdownRequested || _isShutdownPreparing)
        {
            return;
        }

        _windowStateService.SaveNow();
        Hide();
    }

    private void BeginShutdown(bool shouldShutdownTray)
    {
        if (_isShutdownRequested || _isShutdownPreparing)
        {
            return;
        }

        _shouldShutdownTray = shouldShutdownTray;
        AppLogger.Info(shouldShutdownTray ? "Application exit requested" : "Desktop UI exit requested");
        _isShutdownPreparing = true;
        _ = PrepareAndShutdownAsync();
    }

    internal Func<Task>? PrepareShutdownAsync { private get; set; }

    internal Action? OsShutdownDetected { private get; set; }

    // 由宿主注入：重启界面进程，成功后当前窗口自行退出。
    internal Func<Task<bool>>? RestartUiWindowAsync { get; set; }

    internal bool IsShutdownPreparing => _isShutdownPreparing;

    internal bool ShouldShutdownTray => _shouldShutdownTray;

    internal bool CanExitToBackground { get; set; }

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        _windowStateService.SaveNow();
        if (args.CloseReason == WindowCloseReason.OSShutdown)
        {
            OsShutdownDetected?.Invoke();
            AppLogger.Info("OS window close accepted without cancellation");
            base.OnClosing(args);
            return;
        }

        if (!_isShutdownRequested)
        {
            args.Cancel = true;
            if (OperatingSystem.IsMacOS() && args.CloseReason == WindowCloseReason.ApplicationShutdown)
            {
                RequestShutdown();
            }
            else if (DataContext is MainWindowViewModel { AppBehavior.IsMinimizeToTrayEnabled: true }
                && CanExitToBackground)
            {
                HideToBackground();
            }
            else
            {
                RequestShutdown();
            }
            return;
        }

        base.OnClosing(args);
    }

    private async Task PrepareAndShutdownAsync()
    {
        var scope = _shouldShutdownTray ? "Application" : "Desktop UI";
        AppLogger.Info($"{scope} exit preparation started");
        try
        {
            if (PrepareShutdownAsync is not null)
            {
                await PrepareShutdownAsync();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Application exit preparation failed: {exception.Message}");
        }

        AppLogger.Info($"{scope} exit preparation completed");
        var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var requiresExplicitShutdown = desktop?.ShutdownMode == ShutdownMode.OnExplicitShutdown;
        _isShutdownRequested = true;
        Close();
        if (requiresExplicitShutdown)
        {
            desktop?.TryShutdown(0);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SetPageLoadingVisible(false);
        CancelPendingPageTransition();
        _hiddenReleaseTimer.Stop();
        _hiddenReleaseTimer.Tick -= OnHiddenReleaseTimerTick;
        _relativeTimeTimer.Stop();
        _relativeTimeTimer.Tick -= OnRelativeTimeTimerTick;
        _navVisualTimer?.Stop();
        _navVisualTimer = null;
        CloseAccentPicker();
        _visiblePageHost = null;
        ClearPageHostContents();
        if (TryGetPageConverter(out var converter))
        {
            converter.ClearCache();
        }

        DetachViewModel();
        _windowStateService.Dispose();
        DataContextChanged -= OnDataContextChanged;
        PropertyChanged -= OnWindowPropertyChanged;
        Opened -= OnOpened;
        base.OnClosed(e);
    }

    private void ClearPageHostContents()
    {
        foreach (var host in _pageHosts.Values)
        {
            if (host.Content is Control pageContent)
            {
                if (pageContent is IPageContentLifecycle lifecycle)
                {
                    lifecycle.ReleasePageContent();
                }

                pageContent.DataContext = null;
            }

            host.Content = null;
        }
    }

    // 隐藏回收前记录滚动位置，重建后按 AutomationId 还原。
    private void CapturePageScrollOffsets()
    {
        foreach (var (page, host) in _pageHosts)
        {
            if (page == AppNavigationPage.Settings || host.Content is not Control content)
            {
                continue;
            }

            // 尚未重建的页面保留上次快照，已重建的页面以当前状态覆盖。
            _pageScrollOffsets.Remove(page);
            var offsets = content.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .Select(scrollViewer => (Id: AutomationProperties.GetAutomationId(scrollViewer), scrollViewer.Offset))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && (item.Offset.X > 0 || item.Offset.Y > 0))
                .ToDictionary(item => item.Id!, item => item.Offset, StringComparer.Ordinal);
            if (offsets.Count > 0)
            {
                _pageScrollOffsets[page] = offsets;
            }
        }

        if (_pageHosts.TryGetValue(AppNavigationPage.Settings, out var settingsHost)
            && settingsHost.Content is SettingsView settingsView)
        {
            _settingsScrollOffsets = settingsView.CaptureScrollOffsets();
        }
    }

    private void PreparePageLayout(AppNavigationPage page, ContentControl host)
    {
        if (page == AppNavigationPage.Settings && host.Content is SettingsView settingsView)
        {
            if (_settingsScrollOffsets is null)
            {
                return;
            }

            host.UpdateLayout();
            settingsView.RestoreScrollOffsets(_settingsScrollOffsets);
            _settingsScrollOffsets = null;
            return;
        }

        if (!_pageScrollOffsets.Remove(page, out var offsets)
            || host.Content is not Control content)
        {
            return;
        }

        host.UpdateLayout();
        var restoredCount = 0;
        foreach (var scrollViewer in content.GetVisualDescendants().OfType<ScrollViewer>())
        {
            var automationId = AutomationProperties.GetAutomationId(scrollViewer);
            if (automationId is null || !offsets.TryGetValue(automationId, out var offset))
            {
                continue;
            }

            scrollViewer.Offset = offset;
            restoredCount++;
        }

        if (restoredCount > 0)
        {
            host.UpdateLayout();
        }
    }

    private void OnHiddenReleaseTimerTick(object? sender, EventArgs args)
    {
        _hiddenReleaseTimer.Stop();
        if (IsVisible || _isShutdownRequested || _isShutdownPreparing)
        {
            return;
        }

        if (DataContext is MainWindowViewModel { AppBehavior.IsLightweightModeEnabled: true })
        {
            RequestUiShutdown();
            return;
        }

        ReleaseHiddenPageViews();
    }

    private void ReleaseHiddenPageViews()
    {
        CapturePageScrollOffsets();
        _visiblePageHost = null;
        ClearPageHostContents();
        var releasedViewCount = 0;
        if (TryGetPageConverter(out var converter))
        {
            releasedViewCount = converter.ClearCache();
        }

        if (releasedViewCount > 0)
        {
            AppLogger.Debug($"Released {releasedViewCount} hidden page views");
        }

        Dispatcher.UIThread.Post(CollectHiddenMemory, DispatcherPriority.Background);
    }

    private void CollectHiddenMemory()
    {
        if (IsVisible || _isShutdownRequested || _isShutdownPreparing)
        {
            return;
        }

        // 等视觉树解除引用后再压缩托管堆，窗口重新显示时跳过。
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private void OnRelativeTimeTimerTick(object? sender, EventArgs args)
    {
        _attachedViewModel?.SubscriptionPage.RefreshRelativeTimes();
        _attachedViewModel?.OverridePage.RefreshRelativeTimes();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == BoundsProperty || args.Property == WidthProperty)
        {
            UpdateSidebarState();
        }

        if (args.Property == WindowStateProperty)
        {
            UpdateWindowStateVisuals();
            UpdateCaptionButtonsVisibility();
            UpdateBaseColorLayer();
            ReapplyNativeWindowChromeForFullScreen();
        }

        if (args.Property == WindowBase.IsActiveProperty)
        {
            UpdateWindowActiveVisuals();
        }

        if (args.Property != Visual.IsVisibleProperty)
        {
            return;
        }

        if (IsVisible)
        {
            _hiddenReleaseTimer.Stop();
            OnRelativeTimeTimerTick(this, EventArgs.Empty);
            _relativeTimeTimer.Start();
            if (_attachedViewModel is not null)
            {
                // 隐藏可能中断首次加载，恢复时按当前导航重建完整显示状态。
                ShowInitialPage(_attachedViewModel.CurrentPage);
            }

            return;
        }

        _relativeTimeTimer.Stop();
        if (_isShutdownRequested || _isShutdownPreparing)
        {
            return;
        }

        CancelPendingPageTransition();
        DeactivatePageHost(_visiblePageHost);
        _hiddenReleaseTimer.Stop();
        if (_attachedViewModel is { AppBehavior.IsLightweightModeEnabled: true } viewModel)
        {
            var delaySeconds = viewModel.AppBehavior.LightweightModeShutdownDelaySeconds.GetValueOrDefault(
                AppSettings.DefaultLightweightModeShutdownDelaySeconds);
            if (delaySeconds == 0)
            {
                RequestUiShutdown();
                return;
            }

            _hiddenReleaseTimer.Interval = TimeSpan.FromSeconds((double)delaySeconds);
        }
        else
        {
            _hiddenReleaseTimer.Interval = HiddenReleaseDelay;
        }

        _hiddenReleaseTimer.Start();
    }

    private void OnCustomAccentRequested(object? sender, EventArgs args)
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        _activeAccentPicker = new AccentColorPickerView
        {
            DataContext = _attachedViewModel,
            InitialColor = Color.Parse(_attachedViewModel.Theme.CustomAccentColor)
        };
        _activeAccentPicker.Confirmed += OnAccentPickerConfirmed;
        _activeAccentPicker.Cancelled += OnAccentPickerCancelled;

        DialogHost.Show(new DialogPanel { DialogContent = _activeAccentPicker });
    }

    private void OnOpenUpdateReleaseClicked(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"External link open failed: {exception.Message}");
        }
    }

    private void OnAccentPickerConfirmed(object? sender, EventArgs args)
    {
        if (DataContext is MainWindowViewModel viewModel && _activeAccentPicker is not null)
        {
            var color = _activeAccentPicker.SelectedColor;
            viewModel.Theme.ConfirmCustomAccentColor($"#{color.R:X2}{color.G:X2}{color.B:X2}");
        }

        CloseAccentPicker();
    }

    private void OnAccentPickerCancelled(object? sender, EventArgs args)
    {

        CloseAccentPicker();
    }

    private void CloseAccentPicker()
    {
        if (_activeAccentPicker is not null)
        {
            _activeAccentPicker.Confirmed -= OnAccentPickerConfirmed;
            _activeAccentPicker.Cancelled -= OnAccentPickerCancelled;
            _activeAccentPicker = null;
        }

        DialogHost.Close();
    }
}
