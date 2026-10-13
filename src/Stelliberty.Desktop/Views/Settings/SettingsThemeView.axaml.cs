using Avalonia.Controls;
using Avalonia.Interactivity;
using Stelliberty.Application.Diagnostics;
using Stelliberty.Desktop.Localization;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Views.Settings;

public sealed partial class SettingsThemeView : UserControl
{
    public SettingsThemeView()
    {
        InitializeComponent();
    }
    private async void OnChooseBackgroundImageClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        try
        {
            var path = await LocalFilePicker.PickFileAsync(
                topLevel,
                LocalizationManager.Translate("Settings.Background.Choose"),
                LocalizationManager.Translate("Settings.Background.FileFilter"),
                ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]);
            if (!string.IsNullOrWhiteSpace(path))
            {
                viewModel.Theme.SetBackgroundImagePath(path);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, "Background image picker failed");
        }
    }
}
