using Avalonia.Controls;

namespace SBproxy.Desktop.Views;

public sealed partial class CoreLogView : UserControl, IPageContentLifecycle
{
    public CoreLogView()
    {
        InitializeComponent();
    }

    void IPageContentLifecycle.ReleasePageContent()
        => (CoreLogPageRoot.DataContext as SBproxy.Presentation.ViewModels.CoreLogPageViewModel)?.ReleasePresentationRows();
}
