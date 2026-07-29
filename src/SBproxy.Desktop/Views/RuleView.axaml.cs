using Avalonia.Controls;

namespace SBproxy.Desktop.Views;

public sealed partial class RuleView : UserControl, IPageContentLifecycle
{
    public RuleView()
    {
        InitializeComponent();
    }

    void IPageContentLifecycle.ReleasePageContent()
        => (RulePageRoot.DataContext as SBproxy.Presentation.ViewModels.RulePageViewModel)?.ReleasePresentationRows();
}
