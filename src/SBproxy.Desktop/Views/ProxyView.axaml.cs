using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SBproxy.Desktop.Controls;
using SBproxy.Presentation.ViewModels;

namespace SBproxy.Desktop.Views;

public sealed partial class ProxyView : UserControl, IPageContentLifecycle
{
    private static readonly TimeSpan SelectorDropdownCloseDelay = TimeSpan.FromMilliseconds(120);
    private const double CollapsedSelectorHeight = 34;
    private ProxyPageViewModel? _attachedViewModel;
    private bool _isPageContentActive;
    private bool _isPointerOverSelectorRegion;
    private bool _isRefreshingSelectorLayout;
    private bool _isSelectorExpanded;
    private bool _suppressSelectorArrowFocusExpansion;
    private int _selectorDropdownCloseVersion;
    private int _handledLocateNodeRequestId;
    private int _handledScrollToTopRequestId;
    private double _collapsedToolbarHeight;
    private double _expandedSelectorHeight = CollapsedSelectorHeight;
    private double _savedNodeScrollOffset;
    private readonly HashSet<Button> _hiddenSelectorButtons = [];
    private FlyoutBase? _openNodeActionFlyout;

    public ProxyView()
    {
        InitializeComponent();
        SelectorGroupItems.LayoutUpdated += OnSelectorGroupItemsLayoutUpdated;
        ProxyPageRoot.DataContextChanged += OnDataContextChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _openNodeActionFlyout?.Hide();
        _openNodeActionFlyout = null;
        CloseSelectorDropdown();
        _savedNodeScrollOffset = NodeListScroll.Offset.Y;
        DeactivatePageContent();
        DetachViewModel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_isPageContentActive)
        {
            return;
        }

        AttachViewModel();
        RestoreNodeScrollOffset();
    }

    private void RestoreNodeScrollOffset()
    {
        if (_savedNodeScrollOffset <= 0)
        {
            return;
        }

        var offset = _savedNodeScrollOffset;
        Dispatcher.UIThread.Post(
            () => NodeListScroll.Offset = NodeListScroll.Offset.WithY(offset),
            DispatcherPriority.Background);
    }

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_isPageContentActive)
        {
            AttachViewModel();
        }
        else
        {
            DetachViewModel();
        }
    }

    private void AttachViewModel()
    {
        DetachViewModel();

        _attachedViewModel = ProxyPageRoot.DataContext as ProxyPageViewModel;
        _handledLocateNodeRequestId = _attachedViewModel?.LocateNodeRequestId ?? 0;
        _handledScrollToTopRequestId = 0;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (_isPageContentActive)
            {
                _attachedViewModel.ActivatePresentation();
            }
        }
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _attachedViewModel = null;
    }

    void IPageContentLifecycle.ActivatePageContent()
    {
        _isPageContentActive = true;
        AttachViewModel();
        RestoreNodeScrollOffset();
    }

    void IPageContentLifecycle.DeactivatePageContent()
        => DeactivatePageContent();

    private void DeactivatePageContent()
    {
        _isPageContentActive = false;
        CloseSelectorDropdown();
        _attachedViewModel?.DeactivatePresentation();
        DetachViewModel();
    }

    void IPageContentLifecycle.ReleasePageContent()
    {
        var viewModel = _attachedViewModel ?? ProxyPageRoot.DataContext as ProxyPageViewModel;
        DeactivatePageContent();
        viewModel?.ReleasePresentationCache();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not ProxyPageViewModel viewModel)
        {
            return;
        }

        if (args.PropertyName == nameof(ProxyPageViewModel.LocateNodeRequestId)
            && viewModel.LocateNodeRequestId != _handledLocateNodeRequestId)
        {
            _handledLocateNodeRequestId = viewModel.LocateNodeRequestId;
            if (viewModel.LocatedNodeName is not null)
            {
                ScrollToNode(viewModel.LocatedNodeName);
            }
        }

        if (args.PropertyName == nameof(ProxyPageViewModel.ScrollToTopRequestId)
            && viewModel.ScrollToTopRequestId != _handledScrollToTopRequestId)
        {
            _handledScrollToTopRequestId = viewModel.ScrollToTopRequestId;
            ScrollToTop();
        }
    }

    private void ScrollToNode(string nodeName)
    {
        if (_attachedViewModel is null)
        {
            return;
        }

        var index = _attachedViewModel.IndexOfNode(nodeName);
        if (index < 0)
        {
            return;
        }

        NodeList.GetVisualDescendants().OfType<VirtualizingWrapPanel>().FirstOrDefault()?.BringIndexIntoView(index);
    }

    private void ScrollToTop()
    {
        NodeList.UpdateLayout();
        NodeListScroll.Offset = NodeListScroll.Offset.WithY(0);
    }

    private void OnSelectorArrowPointerEntered(object? sender, PointerEventArgs args)
    {
        RefreshSelectorLayout();
        if (_expandedSelectorHeight <= CollapsedSelectorHeight + 1)
        {
            return;
        }

        _isPointerOverSelectorRegion = true;
        _selectorDropdownCloseVersion++;
        SetSelectorExpanded(true);
    }

    private void OnSelectorArrowGotFocus(object? sender, RoutedEventArgs args)
    {
        if (_suppressSelectorArrowFocusExpansion)
        {
            return;
        }

        RefreshSelectorLayout();
        if (_expandedSelectorHeight <= CollapsedSelectorHeight + 1)
        {
            return;
        }

        _selectorDropdownCloseVersion++;
        SetSelectorExpanded(true);
    }

    private void OnSelectorArrowClick(object? sender, RoutedEventArgs args)
    {
        RefreshSelectorLayout();
        if (_expandedSelectorHeight <= CollapsedSelectorHeight + 1)
        {
            return;
        }

        _selectorDropdownCloseVersion++;
        SetSelectorExpanded(true);
    }

    private void OnSelectorGroupItemsLayoutUpdated(object? sender, EventArgs args)
        => RefreshSelectorLayout();

    private void RefreshSelectorLayout()
    {
        if (_isRefreshingSelectorLayout)
        {
            return;
        }

        _isRefreshingSelectorLayout = true;
        try
        {
            var positionedRows = SelectorGroupItems
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("proxy-group-tab")
                    && button.DataContext is ProxyGroupButtonViewModel)
                .Select(button => new
                {
                    Button = button,
                    button.Bounds.Height,
                    Origin = button.TranslatePoint(default, SelectorGroupItems)
                })
                .Where(item => item.Origin is not null)
                .Select(item => new
                {
                    item.Button,
                    Y = item.Origin!.Value.Y,
                    Bottom = item.Origin.Value.Y + item.Height
                })
                .ToList();

            _expandedSelectorHeight = positionedRows.Count == 0
                ? CollapsedSelectorHeight
                : Math.Max(CollapsedSelectorHeight, positionedRows.Max(item => item.Bottom));

            _hiddenSelectorButtons.Clear();
            if (positionedRows.Count > 0)
            {
                var firstRowY = positionedRows.Min(item => item.Y);
                foreach (var item in positionedRows.Where(item => item.Y > firstRowY + 1))
                {
                    _hiddenSelectorButtons.Add(item.Button);
                }
            }

            var hasOverflow = _expandedSelectorHeight > CollapsedSelectorHeight + 1;
            SelectorDropdownArrow.Opacity = hasOverflow ? 1 : 0;
            SelectorDropdownArrow.IsHitTestVisible = hasOverflow;
            SelectorDropdownArrow.IsTabStop = hasOverflow;
            UpdateSelectorTabStops();

            if (_collapsedToolbarHeight <= 0 && ProxyToolbarSurface.Bounds.Height > 0)
            {
                _collapsedToolbarHeight = ProxyToolbarSurface.Bounds.Height;
                ProxyToolbarHost.Height = _collapsedToolbarHeight;
            }

            if (!hasOverflow && _isSelectorExpanded)
            {
                CloseSelectorDropdown();
            }
            else if (_isSelectorExpanded)
            {
                ApplySelectorExpansionHeight();
            }
        }
        finally
        {
            _isRefreshingSelectorLayout = false;
        }
    }

    private void SetSelectorExpanded(bool isExpanded)
    {
        if (_isSelectorExpanded == isExpanded)
        {
            return;
        }

        var moveFocusToArrow = !isExpanded
            && _hiddenSelectorButtons.Any(button => button.IsKeyboardFocusWithin);
        _isSelectorExpanded = isExpanded;
        if (isExpanded)
        {
            UpdateSelectorTabStops();
            ApplySelectorExpansionHeight();
            return;
        }

        SelectorGroupItems.Height = CollapsedSelectorHeight;
        ProxyToolbarSurface.Height = double.NaN;
        UpdateSelectorTabStops();
        if (moveFocusToArrow)
        {
            _suppressSelectorArrowFocusExpansion = true;
            try
            {
                SelectorDropdownArrow.Focus(NavigationMethod.Tab);
            }
            finally
            {
                _suppressSelectorArrowFocusExpansion = false;
            }
        }
    }

    private void UpdateSelectorTabStops()
    {
        foreach (var button in SelectorGroupItems
                     .GetVisualDescendants()
                     .OfType<Button>()
                     .Where(button => button.Classes.Contains("proxy-group-tab")))
        {
            button.IsTabStop = _isSelectorExpanded || !_hiddenSelectorButtons.Contains(button);
        }
    }

    private void ApplySelectorExpansionHeight()
    {
        SelectorGroupItems.Height = _expandedSelectorHeight;
        if (_collapsedToolbarHeight > 0)
        {
            ProxyToolbarSurface.Height =
                _collapsedToolbarHeight + _expandedSelectorHeight - CollapsedSelectorHeight;
        }
    }

    private void OnSelectorRegionPointerEntered(object? sender, PointerEventArgs args)
    {
        _isPointerOverSelectorRegion = true;
        _selectorDropdownCloseVersion++;
    }

    private void OnSelectorRegionPointerExited(object? sender, PointerEventArgs args)
    {
        _isPointerOverSelectorRegion = false;
        ScheduleSelectorDropdownClose();
    }

    private void OnSelectorRegionGotFocus(object? sender, RoutedEventArgs args)
        => _selectorDropdownCloseVersion++;

    private void OnSelectorRegionLostFocus(object? sender, RoutedEventArgs args)
        => ScheduleSelectorDropdownClose();

    private void OnSelectorItemClick(object? sender, RoutedEventArgs args)
        => CloseSelectorDropdown();

    private void ScheduleSelectorDropdownClose()
    {
        var version = ++_selectorDropdownCloseVersion;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _selectorDropdownCloseVersion
                && !_isPointerOverSelectorRegion
                && !SelectorDropdownAnchor.IsKeyboardFocusWithin)
            {
                SetSelectorExpanded(false);
            }
        }, SelectorDropdownCloseDelay);
    }

    private void CloseSelectorDropdown()
    {
        _selectorDropdownCloseVersion++;
        _isPointerOverSelectorRegion = false;
        SetSelectorExpanded(false);
    }

    private void OnNodeActionButtonClick(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: ProxyNodeRowViewModel { IsNode: true } } button
            || FlyoutBase.GetAttachedFlyout(button) is not { } flyout)
        {
            return;
        }

        _openNodeActionFlyout?.Hide();
        _openNodeActionFlyout = flyout;
        FlyoutBase.ShowAttachedFlyout(button);
    }

    private void OnNodeMenuItemClick(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ProxyNodeMenuSelection selection }
            && ProxyPageRoot.DataContext is ProxyPageViewModel viewModel
            && viewModel.NodeMenuActionCommand.CanExecute(selection))
        {
            viewModel.NodeMenuActionCommand.Execute(selection);
        }

        _openNodeActionFlyout?.Hide();
        _openNodeActionFlyout = null;
    }

}
