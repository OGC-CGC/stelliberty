using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Stelliberty.Application.Settings;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Services;

internal sealed class WindowAppearanceService : IDisposable
{
    private MainWindow? _window;
    private SettingsThemeViewModel? _theme;
    private bool _isWindowActive;

    public void Attach(MainWindow window, SettingsThemeViewModel theme)
    {
        if (_theme is not null)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _theme.WindowEffectChanged -= OnWindowEffectChanged;
        }

        if (_window is not null)
        {
            _window.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
        }

        _window = window;
        _theme = theme;
        _isWindowActive = window.IsActive;
        _theme.ThemeChanged += OnThemeChanged;
        _theme.WindowEffectChanged += OnWindowEffectChanged;
        _window.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        _window.Activated += OnWindowActivated;
        _window.Deactivated += OnWindowDeactivated;
        ApplyTheme(theme.SelectedOption.Value);
        ApplyWindowEffect(theme.SelectedWindowEffect);
    }

    public void Reapply()
    {
        if (_theme is null)
        {
            return;
        }

        ApplyTheme(_theme.SelectedOption.Value);
        ApplyWindowEffect(_theme.SelectedWindowEffect);
    }

    public void Dispose()
    {
        if (_theme is not null)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _theme.WindowEffectChanged -= OnWindowEffectChanged;
        }

        if (_window is not null)
        {
            _window.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
        }

        _theme = null;
        _window = null;
        _isWindowActive = false;
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        ApplyTheme(theme);
    }

    private void OnWindowEffectChanged(object? sender, WindowEffect effect)
    {
        ApplyWindowEffect(effect);
    }

    private void OnWindowActivated(object? sender, EventArgs args)
    {
        _isWindowActive = true;
        UpdateRootSurfaceForCurrentEffect(IsCurrentLightTheme());
        KeepMacOSBlurActive();
    }

    private void OnWindowDeactivated(object? sender, EventArgs args)
    {
        _isWindowActive = false;
        UpdateRootSurfaceForCurrentEffect(IsCurrentLightTheme());
        KeepMacOSBlurActive();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs args)
    {

        if (_theme is null || _theme.SelectedOption.Value != AppTheme.System)
        {
            return;
        }

        UpdateRootSurfaceForCurrentEffect(IsCurrentLightTheme());
    }

    private void ApplyTheme(AppTheme theme)
    {
        if (Avalonia.Application.Current is null)
        {
            return;
        }

        Avalonia.Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => Avalonia.Styling.ThemeVariant.Light,
            AppTheme.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default
        };

        UpdateRootSurfaceForCurrentEffect(IsLightTheme(theme));
    }

    private void ApplyWindowEffect(WindowEffect effect)
    {
        if (_window is null)
        {
            return;
        }

        _window.TransparencyLevelHint = effect switch
        {
            WindowEffect.Mica => [WindowTransparencyLevel.Mica],
            WindowEffect.Acrylic => [WindowTransparencyLevel.AcrylicBlur],
            // macOS 后端只把 AcrylicBlur 接到 NSVisualEffectView 模糊。
            WindowEffect.Blur => [WindowTransparencyLevel.AcrylicBlur],
            _ => [WindowTransparencyLevel.None]
        };

        UpdateRootSurfaceForCurrentEffect(IsCurrentLightTheme());
        KeepMacOSBlurActive();
    }

    private bool IsCurrentLightTheme()
    {
        if (_window is null || _theme is null)
        {
            return false;
        }

        return IsLightTheme(_theme.SelectedOption.Value);
    }

    private bool IsLightTheme(AppTheme theme)
    {
        return theme == AppTheme.Light
            || theme == AppTheme.System && _window?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
    }

    private void UpdateRootSurfaceForCurrentEffect(bool isLightTheme)
    {
        if (_window is null || _theme is null)
        {
            return;
        }

        var effect = _theme.SelectedWindowEffect;
        var surfaceBrush = new SolidColorBrush(isLightTheme ? ThemeSurfaceColors.Light : ThemeSurfaceColors.Dark);
        var useInactiveMacOSBlurBrush = OperatingSystem.IsMacOS()
            && effect == WindowEffect.Blur
            && !_isWindowActive;
        var rootSurfaceBrush = effect switch
        {
            WindowEffect.None => surfaceBrush,
            WindowEffect.Acrylic or WindowEffect.Blur => new SolidColorBrush(Color.Parse(
                isLightTheme
                    ? useInactiveMacOSBlurBrush ? "#50FFFFFF" : "#A0FFFFFF"
                    : useInactiveMacOSBlurBrush ? "#97000000" : "#A0000000")),
            _ => (IBrush)Brushes.Transparent
        };
        // 这里只处理窗口效果背景；其余样式归 Theme.axaml。
        _window.Resources["AppRootSurfaceBrush"] = rootSurfaceBrush;
        _window.Resources["AppDialogSurfaceBrush"] = surfaceBrush;
        _window.Resources["AppPopupSurfaceBrush"] = surfaceBrush;
        _window.Resources["ComboBoxPopupBackground"] = surfaceBrush;
    }

    private void KeepMacOSBlurActive()
    {
        if (!OperatingSystem.IsMacOS()
            || _window is null
            || _theme?.SelectedWindowEffect != WindowEffect.Blur)
        {
            return;
        }

        ActivateMacOSVisualEffectViews(_window);
        var attachedWindow = _window;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_window, attachedWindow)
                    && _theme?.SelectedWindowEffect == WindowEffect.Blur)
                {
                    ActivateMacOSVisualEffectViews(attachedWindow);
                }
            },
            DispatcherPriority.Background);
    }

    private static void ActivateMacOSVisualEffectViews(Window window)
    {
        var windowHandle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var contentView = SendIntPtr(windowHandle, RegisterSelector("contentView"));
        var visualEffectViewClass = GetObjectiveCClass("NSVisualEffectView");
        if (contentView == IntPtr.Zero || visualEffectViewClass == IntPtr.Zero)
        {
            return;
        }

        ActivateMacOSVisualEffectViews(contentView, visualEffectViewClass);
    }

    private static void ActivateMacOSVisualEffectViews(IntPtr view, IntPtr visualEffectViewClass)
    {
        if (SendByteIntPtr(view, RegisterSelector("isKindOfClass:"), visualEffectViewClass) != 0)
        {
            SendVoidNInt(view, RegisterSelector("setState:"), 1);
        }

        var subviews = SendIntPtr(view, RegisterSelector("subviews"));
        if (subviews == IntPtr.Zero)
        {
            return;
        }

        var count = SendNUInt(subviews, RegisterSelector("count"));
        var objectAtIndexSelector = RegisterSelector("objectAtIndex:");
        for (nuint index = 0; index < count; index++)
        {
            var subview = SendIntPtrNUInt(subviews, objectAtIndexSelector, index);
            if (subview != IntPtr.Zero)
            {
                ActivateMacOSVisualEffectViews(subview, visualEffectViewClass);
            }
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetObjectiveCClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string className);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string selectorName);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrNUInt(IntPtr receiver, IntPtr selector, nuint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte SendByteIntPtr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidNInt(IntPtr receiver, IntPtr selector, nint value);
}
