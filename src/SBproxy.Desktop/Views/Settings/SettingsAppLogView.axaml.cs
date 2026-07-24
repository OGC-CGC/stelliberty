using Avalonia.Controls;
using SBproxy.Application.Diagnostics;
using SBproxy.Application.Platform;
using SBproxy.Desktop.Localization;
using SBproxy.Presentation.ViewModels;

namespace SBproxy.Desktop.Views.Settings;

public sealed partial class SettingsAppLogView : UserControl
{
    public SettingsAppLogView()
    {
        InitializeComponent();
    }

    private async void OnExportAppLogsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is not Button button
            || DataContext is not MainWindowViewModel viewModel
            || TopLevel.GetTopLevel(button) is not { } topLevel)
        {
            return;
        }

        try
        {
            var exportPath = await LocalFilePicker.PickSaveFileAsync(
                topLevel,
                Localize("AppLogs.SavePicker.Title"),
                $"{AppRuntimeNames.FileNameToken}-applog-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log",
                Localize("AppLogs.SavePicker.Filter"),
                ["*.log", "*.txt"],
                "log");
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                return;
            }

            await viewModel.AppLog.ExportToFileAsync(exportPath);
        }
        catch (Exception exception)
        {
            AppLogger.Error(exception, "App log save picker failed");
        }
    }

    private static string Localize(string key) => LocalizationManager.Translate(key);
}
