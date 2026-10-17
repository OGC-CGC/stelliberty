using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Stelliberty.Desktop.Services;

namespace Stelliberty.Desktop.Views.Dialogs;

public sealed partial class AccentColorPickerView : UserControl
{
    private const double PickerLightness = 0.7;
    private bool _updatingControls;

    public AccentColorPickerView()
    {
        InitializeComponent();
        PART_HueSlider.ValueChanged += OnComponentChanged;
        PART_ChromaSlider.ValueChanged += OnComponentChanged;
        PART_LightPreview.Background = new SolidColorBrush(ThemeSurfaceColors.Light);
        PART_DarkPreview.Background = new SolidColorBrush(ThemeSurfaceColors.Dark);
        UpdatePicker();
    }

    public Color SelectedColor => AccentColorPaletteGenerator.FromOklch(
        PickerLightness, PART_ChromaSlider.Value, PART_HueSlider.Value);

    public Color InitialColor
    {
        get => SelectedColor;
        set
        {
            var color = AccentColorPaletteGenerator.ToOklch(value);
            _updatingControls = true;
            PART_HueSlider.Value = double.IsNaN(color.Hue) ? 0 : color.Hue;
            PART_ChromaSlider.Maximum = AccentColorPaletteGenerator.GetMaximumChroma(
                PickerLightness, PART_HueSlider.Value);
            PART_ChromaSlider.Value = Math.Min(color.Chroma, PART_ChromaSlider.Maximum);
            _updatingControls = false;
            UpdatePicker();
        }
    }

    public event EventHandler? Confirmed;
    public event EventHandler? Cancelled;

    private void OnComponentChanged(object? sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_updatingControls)
        {
            UpdatePicker();
        }
    }

    private void UpdatePicker()
    {
        _updatingControls = true;
        var hue = PART_HueSlider.Value;
        PART_ChromaSlider.Maximum = AccentColorPaletteGenerator.GetMaximumChroma(PickerLightness, hue);
        PART_ChromaSlider.Value = Math.Min(PART_ChromaSlider.Value, PART_ChromaSlider.Maximum);
        _updatingControls = false;

        var hueBrush = CreateGradient();
        var chromaBrush = CreateGradient();
        for (var i = 0; i <= 72; i++)
        {
            var offset = i / 72d;
            var sampleHue = offset * 360;
            var sampleChroma = AccentColorPaletteGenerator.GetMaximumChroma(PickerLightness, sampleHue);
            hueBrush.GradientStops.Add(new GradientStop(
                AccentColorPaletteGenerator.FromOklch(PickerLightness, sampleChroma, sampleHue), offset));
            chromaBrush.GradientStops.Add(new GradientStop(
                AccentColorPaletteGenerator.FromOklch(PickerLightness, offset * PART_ChromaSlider.Maximum, hue), offset));
        }
        PART_HueSlider.Background = hueBrush;
        PART_ChromaSlider.Background = chromaBrush;

        ApplyPreview(AccentColorPaletteGenerator.Generate(SelectedColor, true),
            PART_LightAccent, PART_LightOnAccent, PART_LightSelected, PART_LightSelectedText);
        ApplyPreview(AccentColorPaletteGenerator.Generate(SelectedColor, false),
            PART_DarkAccent, PART_DarkOnAccent, PART_DarkSelected, PART_DarkSelectedText);
    }

    private static LinearGradientBrush CreateGradient() => new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
    };

    private static void ApplyPreview(AccentColorPalette palette,
        Border accent, TextBlock onAccent, Border selected, TextBlock selectedText)
    {
        accent.Background = new SolidColorBrush(palette.Accent);
        onAccent.Foreground = new SolidColorBrush(palette.OnAccent);
        selected.Background = new SolidColorBrush(palette.Subtle, AccentColorPalette.SubtleOpacity);
        selected.BorderBrush = new SolidColorBrush(palette.Border, AccentColorPalette.BorderOpacity);
        selectedText.Foreground = new SolidColorBrush(palette.Accent);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs args)
    {
        Confirmed?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs args)
    {
        Cancelled?.Invoke(this, EventArgs.Empty);
    }
}
