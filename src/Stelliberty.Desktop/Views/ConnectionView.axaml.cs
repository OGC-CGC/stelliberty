using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Stelliberty.Application.Diagnostics;

namespace Stelliberty.Desktop.Views;

public sealed partial class ConnectionView : UserControl
{
    public ConnectionView()
    {
        InitializeComponent();
    }

    private async void OnCopyValueClicked(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (sender is Button { CommandParameter: string value } button
                && TopLevel.GetTopLevel(button)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(value);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Connection detail clipboard write failed: {exception.Message}");
        }
    }
}
