using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Stelliberty.Application.Diagnostics;
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
    private Border? _backgroundImageLayer;
    private Bitmap? _backgroundBitmap;
    private int _backgroundLoadVersion;

    public void Attach(MainWindow window, SettingsThemeViewModel theme)
    {
        if (_theme is not null)
        {
            _theme.BackgroundImageChanged -= OnBackgroundImageChanged;
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

        ReleaseBackgroundImage();
        _window = window;
        _backgroundImageLayer = window.FindControl<Border>("BackgroundImageLayer");
        _theme = theme;
        _isWindowActive = window.IsActive;
        _theme.BackgroundImageChanged += OnBackgroundImageChanged;
        _theme.ThemeChanged += OnThemeChanged;
        _theme.WindowEffectChanged += OnWindowEffectChanged;
        _window.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        _window.Activated += OnWindowActivated;
        _window.Deactivated += OnWindowDeactivated;
        _window.Opened += OnWindowOpened;
        ApplyTheme(theme.SelectedOption.Value);
        ApplyWindowEffect(theme.SelectedWindowEffect);
        UpdateBackgroundImage(reloadImage: true);
    }

    public void Reapply()
    {
        if (_theme is null)
        {
            return;
        }

        ApplyTheme(_theme.SelectedOption.Value);
        ApplyWindowEffect(_theme.SelectedWindowEffect);
        _backgroundImageLayer = _window?.FindControl<Border>("BackgroundImageLayer");
        UpdateBackgroundImage(reloadImage: true);
    }

    public void Dispose()
    {
        if (_theme is not null)
        {
            _theme.BackgroundImageChanged -= OnBackgroundImageChanged;
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

        ReleaseBackgroundImage();
        _backgroundImageLayer = null;
        _macOSGlassEffectService?.Dispose();
        _theme = null;
        _window = null;
        _isWindowActive = false;
    }

    private void OnBackgroundImageChanged(object? sender, bool reloadImage)
        => UpdateBackgroundImage(reloadImage);

    private void UpdateBackgroundImage(bool reloadImage)
    {
        if (_theme is null || _backgroundImageLayer is null)
        {
            return;
        }

        if (!reloadImage)
        {
            ApplyBackgroundImage();
            return;
        }

        ReleaseBackgroundImage();
        _theme.SetBackgroundImageLoadFailed(false);
        if (_theme.HasBackgroundImage)
        {
            _ = LoadBackgroundImageAsync(_theme.BackgroundImagePath, _backgroundLoadVersion);
        }
    }

    private async Task LoadBackgroundImageAsync(string path, int version)
    {
        // 解码在后台进行；快速替换图片或关闭窗口后，丢弃旧加载结果。
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = path == AppSettings.DefaultBackgroundImagePath
                    ? AssetLoader.Open(new Uri("avares://stelliberty_ui/Assets/backgrounds/default.png"))
                    : File.OpenRead(path);
                return new Bitmap(stream);
            }
            catch (Exception exception)
            {
                AppLogger.Error(exception, "Background image load failed");
                return null;
            }
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _backgroundLoadVersion || _theme is null || _backgroundImageLayer is null)
            {
                bitmap?.Dispose();
                return;
            }

            _backgroundBitmap = bitmap;
            _theme.SetBackgroundImageLoadFailed(bitmap is null);
            ApplyBackgroundImage();
        });
    }

    private void ApplyBackgroundImage()
    {
        if (_theme is null || _backgroundImageLayer is null)
        {
            return;
        }

        _backgroundImageLayer.Background = _backgroundBitmap is null ? null : new ImageBrush
        {
            Source = _backgroundBitmap,
            Stretch = _theme.BackgroundStretch switch
            {
                BackgroundImageStretchMode.None => Stretch.None,
                BackgroundImageStretchMode.Uniform => Stretch.Uniform,
                BackgroundImageStretchMode.Fill => Stretch.Fill,
                _ => Stretch.UniformToFill
            },
            AlignmentX = _theme.BackgroundHorizontalAlignment switch
            {
                BackgroundImageHorizontalAlignment.Left => AlignmentX.Left,
                BackgroundImageHorizontalAlignment.Right => AlignmentX.Right,
                _ => AlignmentX.Center
            },
            AlignmentY = _theme.BackgroundVerticalAlignment switch
            {
                BackgroundImageVerticalAlignment.Top => AlignmentY.Top,
                BackgroundImageVerticalAlignment.Bottom => AlignmentY.Bottom,
                _ => AlignmentY.Center
            },
            Opacity = 1 - _theme.BackgroundImageTransparency / 100d
        };
    }

    private void ReleaseBackgroundImage()
    {
        _backgroundLoadVersion++;
        if (_backgroundImageLayer is not null)
        {
            _backgroundImageLayer.Background = null;
        }
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
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
        var surfaceBrush = new SolidColorBrush(Color.Parse(effect == WindowEffect.FrutigerAero
            ? isLightTheme ? "#99FFFFFF" : "#99000000"
            : isLightTheme ? "#EEFFFFFF" : "#EE000000"));
        var cardSurfaceBrush = new SolidColorBrush(Color.Parse(effect == WindowEffect.FrutigerAero
            ? isLightTheme ? "#66FFFFFF" : "#66000000"
            : isLightTheme ? "#CCFFFFFF" : "#BB191919"));
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
        // 这里只处理窗口效果背景；其余样式归 Theme.axaml。
        _window.Resources["AppRootSurfaceBrush"] = rootSurfaceBrush;
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
