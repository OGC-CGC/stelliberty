using Avalonia.Controls;

namespace SBproxy.Desktop.Views;

public sealed partial class ConnectionView : UserControl, IPageContentLifecycle
{
    public ConnectionView()
    {
        InitializeComponent();
    }

    void IPageContentLifecycle.ReleasePageContent()
        => (ConnectionPageRoot.DataContext as SBproxy.Presentation.ViewModels.ConnectionPageViewModel)?.ReleasePresentationRows();
}
