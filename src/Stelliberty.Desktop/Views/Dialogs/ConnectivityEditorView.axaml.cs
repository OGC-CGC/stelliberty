using Avalonia.Controls;
using Avalonia.Input;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Views.Dialogs;

public sealed partial class ConnectivityEditorView : UserControl
{
    public ConnectivityEditorView() => InitializeComponent();

    private void OnEditorKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && args.KeyModifiers == KeyModifiers.None
            && DataContext is HomePageViewModel viewModel)
        {
            viewModel.CancelConnectivityEditCommand.Execute(null);
            args.Handled = true;
        }
    }
}
