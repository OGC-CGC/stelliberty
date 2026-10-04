using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Stelliberty.Application.Settings;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Services;

internal sealed class WindowAppearanceService : IDisposable
{
    private readonly MacOSGlassEffectService? _macOSGlassEffectService = OperatingSystem.IsMacOS()
        ? new MacOSGlassEffectService()
        : null;
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
            _window.Opened -= OnWindowOpened;
        }

        _window = window;
        _theme = theme;
        _isWindowActive = window.IsActive;
        _theme.ThemeChanged += OnThemeChanged;
        _theme.WindowEffectChanged += OnWindowEffectChanged;
        _window.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        _window.Activated += OnWindowActivated;
        _window.Deactivated += OnWindowDeactivated;
        _window.Opened += OnWindowOpened;
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
            _window.Opened -= OnWindowOpened;
        }

        _macOSGlassEffectService?.Dispose();
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

    private void OnWindowOpened(object? sender, EventArgs args)
    {
        var window = _window;
        var theme = _theme;
        if (window is null || theme is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!ReferenceEquals(_window, window) || !ReferenceEquals(_theme, theme))
                {
                    return;
                }

                if (!OperatingSystem.IsMacOS())
                {
                    _isWindowActive = window.IsActive;
                    ApplyWindowEffect(theme.SelectedWindowEffect);
                    return;
                }

                _macOSGlassEffectService?.Remove();
                window.TransparencyLevelHint = [WindowTransparencyLevel.None];
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (!ReferenceEquals(_window, window) || !ReferenceEquals(_theme, theme))
                        {
                            return;
                        }

                        _isWindowActive = window.IsActive;
                        ApplyWindowEffect(theme.SelectedWindowEffect);
                    },
                    DispatcherPriority.Background);
            },
            DispatcherPriority.Render);
    }

    private void OnWindowActivated(object? sender, EventArgs args)
    {
        _isWindowActive = true;
        if (OperatingSystem.IsMacOS()
            && _theme?.SelectedWindowEffect is WindowEffect.Blur or WindowEffect.FrutigerAero)
        {
            UpdateRootSurfaceForCurrentEffect(IsCurrentLightTheme());
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs args)
    {
        _isWindowActive = false;
        if (OperatingSystem.IsMacOS()
            && _theme?.SelectedWindowEffect is WindowEffect.Blur or WindowEffect.FrutigerAero)
        {
            UpdateRootSurfaceForCurrentEffect(IsCurrentLightTheme());
        }
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

        var useMacOSGlass = OperatingSystem.IsMacOS()
                            && effect == WindowEffect.FrutigerAero
                            && MacOSGlassEffectService.IsSupported;
        _window.Classes.Set("frutiger-aero", useMacOSGlass);
        if (!useMacOSGlass)
        {
            _macOSGlassEffectService?.Remove();
        }

        _window.TransparencyLevelHint = effect switch
        {
            WindowEffect.Mica => [WindowTransparencyLevel.Mica],
            WindowEffect.Acrylic => [WindowTransparencyLevel.AcrylicBlur],
            WindowEffect.FrutigerAero when useMacOSGlass => [WindowTransparencyLevel.Transparent],
            WindowEffect.Blur => [WindowTransparencyLevel.AcrylicBlur],
            _ => [WindowTransparencyLevel.None]
        };

        if (useMacOSGlass && _window.IsVisible)
        {
            _macOSGlassEffectService?.Apply(_window);
        }

        UpdateRootSurfaceForCurrentEffect(IsCurrentLightTheme());
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
        var cardSurfaceBrush = new SolidColorBrush(Color.Parse(effect == WindowEffect.FrutigerAero
            ? isLightTheme ? "#22FFFFFF" : "#22000000"
            : isLightTheme ? "#CCFFFFFF" : "#44000000"));
        var rootSurfaceBrush = effect switch
        {
            WindowEffect.None => surfaceBrush,
            WindowEffect.FrutigerAero when OperatingSystem.IsMacOS() => Brushes.Transparent,
            WindowEffect.Blur when OperatingSystem.IsMacOS() => new SolidColorBrush(Color.Parse(
                isLightTheme
                    ? _isWindowActive ? "#A0FFFFFF" : "#FFEEEEEE"
                    : _isWindowActive ? "#A0000000" : "#FF242424")),
            WindowEffect.Acrylic or WindowEffect.Blur => new SolidColorBrush(Color.Parse(isLightTheme ? "#B3FFFFFF" : "#B3212121")),
            _ => (IBrush)Brushes.Transparent
        };
        IBrush edgeHighlightBrush = OperatingSystem.IsMacOS() && effect == WindowEffect.FrutigerAero
            ? new SolidColorBrush(Color.Parse(_isWindowActive ? "#B8FFFFFF" : "#66FFFFFF"))
            : Brushes.Transparent;
        // 这里只处理窗口效果背景；其余样式归 Theme.axaml。
        _window.Resources["AppRootSurfaceBrush"] = rootSurfaceBrush;
        _window.Resources["AppWindowEdgeHighlightBrush"] = edgeHighlightBrush;
        _window.Resources["AppDialogSurfaceBrush"] = surfaceBrush;
        _window.Resources["AppPopupSurfaceBrush"] = surfaceBrush;
        _window.Resources["ComboBoxPopupBackground"] = surfaceBrush;
        _window.Resources["AppSurfaceBrush"] = cardSurfaceBrush;
        _window.Resources["AppCardBrush"] = cardSurfaceBrush;
        _window.Resources["AppSettingsGroupBrush"] = cardSurfaceBrush;
        _window.Resources["AppSurfaceRaisedShadow"] = BoxShadows.Parse(effect == WindowEffect.FrutigerAero
            ? isLightTheme
                ? "inset 1 1 2 0 #B8FFFFFF, 0 12 32 -18 #40000000"
                : "inset 1 1 2 0 #8AFFFFFF, 0 12 32 -18 #66000000"
            : isLightTheme
                ? "0 12 32 -18 #40000000"
                : "0 12 32 -18 #66000000");
        _window.Resources["AppSurfaceListShadow"] = BoxShadows.Parse(effect == WindowEffect.FrutigerAero
            ? isLightTheme
                ? "inset 1 1 2 0 #9FFFFFFF, 0 4 14 -12 #2A000000"
                : "inset 1 1 2 0 #70FFFFFF, 0 4 14 -12 #48000000"
            : isLightTheme
                ? "0 4 14 -12 #2A000000"
                : "0 4 14 -12 #48000000");
        _window.Resources["AppSidebarShadow"] = BoxShadows.Parse(effect == WindowEffect.None
            ? _window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light
                ? "12 0 28 -18 #24000000, 2 0 8 -4 #22000000"
                : "12 0 28 -18 #32FFFFFF, 2 0 8 -4 #26FFFFFF"
            : isLightTheme
                ? "14 0 34 -16 #3A000000, 3 0 10 -4 #24000000"
                : "14 0 34 -16 #38FFFFFF, 3 0 10 -4 #28FFFFFF");
    }
}
