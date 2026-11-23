using Avalonia;
using Avalonia.Controls;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Views.Settings;

public sealed partial class SettingsNetworkView : UserControl
{
    public SettingsNetworkView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as MainWindowViewModel)?.CoreConfig.RefreshOutboundInterfaceChoices();
    }
}
