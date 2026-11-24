using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Stelliberty.Application.Diagnostics;
using Stelliberty.Application.Settings;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Services;

internal sealed class WindowAppearanceService(Action<Color?> setBackgroundAccent) : IDisposable
{
    private readonly MacOSGlassEffectService? _macOSGlassEffectService = OperatingSystem.IsMacOS()
        ? new MacOSGlassEffectService()
        : null;
    private MainWindow? _window;
    private SettingsThemeViewModel? _theme;
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
            _window.Opened -= OnWindowOpened;
        }

        ReleaseBackgroundImage();
        _window = window;
        SetBackgroundImageLayer(window.FindControl<Border>("BackgroundImageLayer"));
        _theme = theme;
        _theme.BackgroundImageChanged += OnBackgroundImageChanged;
        _theme.ThemeChanged += OnThemeChanged;
        _theme.WindowEffectChanged += OnWindowEffectChanged;
        _window.ActualThemeVariantChanged += OnActualThemeVariantChanged;
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
        SetBackgroundImageLayer(_window?.FindControl<Border>("BackgroundImageLayer"));
        UpdateBackgroundImage(reloadImage: true);
    }

    // 进出系统全屏后 AppKit 会重建标题栏，重新应用窗口效果（含 macOS 玻璃与标题栏材质/下划线隐藏）。
    public void ReapplyWindowEffect()
    {
        if (_theme is null)
        {
            return;
        }

        ApplyWindowEffect(_theme.SelectedWindowEffect);
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
            _window.Opened -= OnWindowOpened;
        }

        ReleaseBackgroundImage();
        SetBackgroundImageLayer(null);
        _macOSGlassEffectService?.Dispose();
        _theme = null;
        _window = null;
    }

    private void OnBackgroundImageChanged(object? sender, bool reloadImage)
        => UpdateBackgroundImage(reloadImage);

    private void SetBackgroundImageLayer(Border? layer)
    {
        if (ReferenceEquals(_backgroundImageLayer, layer))
        {
            return;
        }

        if (_backgroundImageLayer is { } previous)
        {
            previous.SizeChanged -= OnBackgroundImageLayerSizeChanged;
            previous.Background = null;
        }

        _backgroundImageLayer = layer;
        if (layer is not null)
        {
            layer.SizeChanged += OnBackgroundImageLayerSizeChanged;
        }
    }

    private void OnBackgroundImageLayerSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (sender is Border { Background: DrawingBrush { TileMode: TileMode.Tile } brush }
            && _backgroundBitmap is { } bitmap)
        {
            UpdateTileDestination(brush, args.NewSize, bitmap.Size);
        }
    }

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
        if (_theme.HasBackgroundImage)
        {
            _ = LoadBackgroundImageAsync(_theme.BackgroundImagePath, _backgroundLoadVersion);
        }
    }

    private async Task LoadBackgroundImageAsync(string path, int version)
    {
        // 解码在后台进行；快速替换图片或关闭窗口后，丢弃旧加载结果。
        var (bitmap, accent) = await Task.Run<(Bitmap?, Color?)>(() =>
        {
            Bitmap bitmap;
            try
            {
                using var stream = path == AppSettings.DefaultBackgroundImagePath
                    ? AssetLoader.Open(new Uri("avares://stelliberty_ui/Assets/backgrounds/default.png"))
                    : File.OpenRead(path);
                bitmap = new Bitmap(stream);
            }
            catch (Exception exception)
            {
                AppLogger.Error(exception, "Background image load failed");
                return (null, null);
            }

            try
            {
                return (bitmap, BackgroundImageAccentExtractor.Extract(bitmap));
            }
            catch (Exception exception)
            {
                AppLogger.Warning($"Background image accent extraction failed: {exception.Message}");
                return (bitmap, null);
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
            ApplyBackgroundImage();
            setBackgroundAccent(accent);
            if (bitmap is null && _window?.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.ShowErrorToast(_theme.BackgroundImageErrorText);
            }
        });
    }

    private void ApplyBackgroundImage()
    {
        if (_theme is null || _backgroundImageLayer is null)
        {
            return;
        }

        if (_backgroundBitmap is not { } bitmap)
        {
            _backgroundImageLayer.Background = null;
            return;
        }

        var horizontalAlignment = _theme.BackgroundHorizontalAlignment switch
        {
            BackgroundImageHorizontalAlignment.Left => AlignmentX.Left,
            BackgroundImageHorizontalAlignment.Right => AlignmentX.Right,
            _ => AlignmentX.Center
        };
        var verticalAlignment = _theme.BackgroundVerticalAlignment switch
        {
            BackgroundImageVerticalAlignment.Top => AlignmentY.Top,
            BackgroundImageVerticalAlignment.Bottom => AlignmentY.Bottom,
            _ => AlignmentY.Center
        };
        var opacity = 1 - _theme.BackgroundImageTransparency / 100d;
        if (_theme.BackgroundStretch == BackgroundImageStretchMode.Tile)
        {
            var tileBrush = new DrawingBrush
            {
                Drawing = new ImageDrawing { ImageSource = bitmap, Rect = new Rect(bitmap.Size) },
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                AlignmentX = horizontalAlignment,
                AlignmentY = verticalAlignment,
                Opacity = opacity
            };
            UpdateTileDestination(tileBrush, _backgroundImageLayer.Bounds.Size, bitmap.Size);
            _backgroundImageLayer.Background = tileBrush;
            return;
        }

        _backgroundImageLayer.Background = new ImageBrush
        {
            Source = bitmap,
            Stretch = _theme.BackgroundStretch switch
            {
                BackgroundImageStretchMode.None => Stretch.None,
                BackgroundImageStretchMode.Uniform => Stretch.Uniform,
                BackgroundImageStretchMode.Fill => Stretch.Fill,
                _ => Stretch.UniformToFill
            },
            AlignmentX = horizontalAlignment,
            AlignmentY = verticalAlignment,
            Opacity = opacity
        };
    }

    private static void UpdateTileDestination(DrawingBrush brush, Size area, Size tile)
    {
        var remainingWidth = area.Width - tile.Width;
        var remainingHeight = area.Height - tile.Height;
        var x = brush.AlignmentX switch
        {
            AlignmentX.Right => remainingWidth,
            AlignmentX.Center => remainingWidth / 2,
            _ => 0
        };
        var y = brush.AlignmentY switch
        {
            AlignmentY.Bottom => remainingHeight,
            AlignmentY.Center => remainingHeight / 2,
            _ => 0
        };
        brush.DestinationRect = new RelativeRect(x, y, tile.Width, tile.Height, RelativeUnit.Absolute);
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
        setBackgroundAccent(null);
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

                        ApplyWindowEffect(theme.SelectedWindowEffect);
                    },
                    DispatcherPriority.Background);
            },
            DispatcherPriority.Render);
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
        AppLogger.Info($"Window effect applied: requested={effect} actual={_window.ActualTransparencyLevel} theme={_window.ActualThemeVariant}");
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
            ? isLightTheme ? "#66FFFFFF" : "#66000000"
            : isLightTheme ? "#EEFFFFFF" : "#EE000000"));
        const string darkAeroCardColor = "#99000000";
        var cardSurfaceBrush = new SolidColorBrush(Color.Parse(effect switch
        {
            WindowEffect.FrutigerAero => isLightTheme ? "#CCFFFFFF" : darkAeroCardColor,
            WindowEffect.Acrylic => isLightTheme ? "#CCFFFFFF" : "#CC000000",
            _ => isLightTheme ? "#CCFFFFFF" : "#BB191919"
        }));
        var cardBackgroundBrush = !isLightTheme && OperatingSystem.IsMacOS() && effect == WindowEffect.Blur
            ? new SolidColorBrush(Color.Parse(darkAeroCardColor))
            : cardSurfaceBrush;
        var rootSurfaceBrush = effect switch
        {
            WindowEffect.None =>
                new SolidColorBrush(Color.Parse(isLightTheme ? "#EFEFEF" : "#212121")),
            WindowEffect.FrutigerAero when OperatingSystem.IsMacOS() => Brushes.Transparent,
            WindowEffect.Blur when OperatingSystem.IsMacOS() => new SolidColorBrush(Color.Parse(
                isLightTheme ? "#A0FFFFFF" : "#A0191919")),
            // Zen 工具栏使用冷灰/近黑底色和 60% 不透明度。
            WindowEffect.Acrylic => new SolidColorBrush(Color.Parse(isLightTheme ? "#99F0F0F4" : "#9917171A")),
            WindowEffect.Blur => new SolidColorBrush(Color.Parse(isLightTheme ? "#B3FFFFFF" : "#B3212121")),
            _ => (IBrush)Brushes.Transparent
        };
        // 这里只处理窗口效果背景；其余样式归 Theme.axaml。
        _window.Resources["AppRootSurfaceBrush"] = rootSurfaceBrush;
        _window.Resources["AppDialogSurfaceBrush"] = surfaceBrush;
        _window.Resources["AppPopupSurfaceBrush"] = surfaceBrush;
        _window.Resources["ComboBoxPopupBackground"] = surfaceBrush;
        _window.Resources["AppSurfaceBrush"] = cardSurfaceBrush;
        _window.Resources["AppCardBrush"] = cardBackgroundBrush;
        _window.Resources["AppSettingsGroupBrush"] = cardBackgroundBrush;
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
