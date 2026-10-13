using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Stelliberty.Desktop.Controls;
using Stelliberty.Presentation.ViewModels;

namespace Stelliberty.Desktop.Views;

public sealed partial class ProxyView : UserControl, IPageContentLifecycle
{
    private static readonly TimeSpan SelectorDropdownCloseDelay = TimeSpan.FromMilliseconds(120);
    private const double CollapsedSelectorHeight = 34;
    private bool _isPointerOverSelectorRegion;
    private bool _isRefreshingSelectorLayout;
    private bool _isSelectorExpanded;
    private bool _suppressSelectorArrowFocusExpansion;
    private int _selectorDropdownCloseVersion;
    private double _collapsedToolbarHeight;
    private double _expandedSelectorHeight = CollapsedSelectorHeight;
    private readonly HashSet<Button> _hiddenSelectorButtons = [];
    private static readonly TimeSpan GroupExpandDuration = TimeSpan.FromMilliseconds(220);
    private readonly Dictionary<Border, GroupContentAnimationState> _groupContentAnimations = [];
    private CancellationTokenSource _groupContentSequenceCancellation = new();
    private int _groupContentSyncVersion;
    private string? _pendingGroupNavigation;
    private ProxyPageViewModel? _attachedViewModel;
    private bool _isPageContentActive;
    private int _handledLocateNodeRequestId;
    private int _handledScrollToTopRequestId;
    private double _savedNodeScrollOffset;

    public ProxyView()
    {
        InitializeComponent();
        SelectorGroupItems.LayoutUpdated += OnSelectorGroupItemsLayoutUpdated;
        ProxyPageRoot.DataContextChanged += OnDataContextChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
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
            _attachedViewModel.GroupNavigationRequested += OnGroupNavigationRequested;
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

        _pendingGroupNavigation = null;
        _attachedViewModel.GroupNavigationRequested -= OnGroupNavigationRequested;
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
        CloseSelectorDropdown();
        _groupContentSequenceCancellation.Cancel();
        foreach (var (content, state) in _groupContentAnimations)
        {
            SetGroupContentState(content, state.Card.IsExpanded);
            ((Border)content.Child!).Height = double.NaN;
        }

        _isPageContentActive = false;
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

        if (ProxyPageRoot.DataContext is ProxyPageViewModel { IsVerticalLayout: true })
        {
            CloseSelectorDropdown();
            _collapsedToolbarHeight = 0;
            ProxyToolbarHost.Height = double.NaN;
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

            if (!_isSelectorExpanded && ProxyToolbarSurface.Bounds.Height > 0)
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
        SelectorDropdownArrow.Classes.Set("expanded", isExpanded);
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

    private void OnGroupContentAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is not Border content || content.DataContext is not ProxyGroupCardViewModel card)
        {
            return;
        }

        RemoveGroupContentAnimation(content);
        PropertyChangedEventHandler handler = (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ProxyGroupCardViewModel.IsExpanded))
            {
                QueueGroupContentSynchronization();
            }
        };
        _groupContentAnimations[content] = new GroupContentAnimationState(card, handler);
        card.PropertyChanged += handler;
        SetGroupContentState(content, card.IsExpanded);
    }

    private void OnGroupContentDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is Border content)
        {
            _groupContentSequenceCancellation.Cancel();
            RemoveGroupContentAnimation(content);
        }
    }

    private void OnGroupNavigationRequested(object? sender, string groupName)
    {
        _pendingGroupNavigation = groupName;
        QueueGroupContentSynchronization();
    }

    private void ScrollToPendingGroup()
    {
        var groupName = _pendingGroupNavigation;
        _pendingGroupNavigation = null;
        if (groupName is null
            || _attachedViewModel is not { IsVerticalLayout: true } viewModel
            || !string.Equals(viewModel.ExpandedGroupName, groupName, StringComparison.Ordinal))
        {
            return;
        }

        GroupCardScroll.UpdateLayout();
        var card = GroupCardList.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("group")
                && border.DataContext is ProxyGroupCardViewModel group
                && string.Equals(group.Name, groupName, StringComparison.Ordinal));
        if (card?.TranslatePoint(default, GroupCardList) is not { } position)
        {
            return;
        }

        var maxOffset = Math.Max(0, GroupCardScroll.Extent.Height - GroupCardScroll.Viewport.Height);
        GroupCardScroll.Offset = GroupCardScroll.Offset.WithY(Math.Clamp(position.Y, 0, maxOffset));
    }

    private void QueueGroupContentSynchronization()
    {
        var syncVersion = ++_groupContentSyncVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (syncVersion == _groupContentSyncVersion)
            {
                _ = SynchronizeGroupContentAsync();
            }
        }, DispatcherPriority.Background);
    }

    private async Task SynchronizeGroupContentAsync()
    {
        _groupContentSequenceCancellation.Cancel();
        _groupContentSequenceCancellation.Dispose();
        _groupContentSequenceCancellation = new CancellationTokenSource();
        var cancellationToken = _groupContentSequenceCancellation.Token;
        var targetContent = _groupContentAnimations
            .Where(item => item.Value.Card.IsExpanded)
            .Select(item => item.Key)
            .FirstOrDefault();

        foreach (var content in _groupContentAnimations.Keys.Where(item => item != targetContent && item.IsVisible).ToList())
        {
            if (!await AnimateGroupContentAsync(content, false, cancellationToken))
            {
                return;
            }
        }

        if (targetContent is not null
            && _groupContentAnimations.TryGetValue(targetContent, out var state)
            && state.Card.IsExpanded
            && (!targetContent.IsVisible || !double.IsPositiveInfinity(targetContent.MaxHeight)))
        {
            if (!await AnimateGroupContentAsync(targetContent, true, cancellationToken))
            {
                return;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            ScrollToPendingGroup();
        }
    }

    private async Task<bool> AnimateGroupContentAsync(
        Border content,
        bool isExpanded,
        CancellationToken cancellationToken)
    {
        var surface = (Border)content.Child!;
        var startHeight = content.IsVisible ? content.Bounds.Height : 0;
        var startOpacity = content.IsVisible ? content.Opacity : 0;
        var endHeight = isExpanded ? MeasureExpandedHeight(content, surface) : 0;
        surface.Height = isExpanded ? endHeight : startHeight;
        content.IsVisible = true;
        content.IsHitTestVisible = false;
        content.MaxHeight = startHeight;
        content.Opacity = startOpacity;

        var animation = new Animation
        {
            Duration = GroupExpandDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(MaxHeightProperty, startHeight),
                        new Setter(OpacityProperty, startOpacity),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(MaxHeightProperty, endHeight),
                        new Setter(OpacityProperty, isExpanded ? 1d : 0d),
                    },
                },
            },
        };

        try
        {
            await animation.RunAsync(content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            surface.Height = double.NaN;
            return false;
        }

        if (isExpanded)
        {
            content.MaxHeight = double.PositiveInfinity;
            content.Opacity = 1;
            content.IsHitTestVisible = true;
            surface.Height = double.NaN;
        }
        else
        {
            SetGroupContentState(content, false);
            surface.Height = double.NaN;
        }

        return true;
    }

    private static double MeasureExpandedHeight(Border content, Border surface)
    {
        content.IsVisible = true;
        content.MaxHeight = double.PositiveInfinity;
        surface.Height = double.NaN;
        var availableWidth = (content.Parent as Control)?.Bounds.Width ?? content.Bounds.Width;
        surface.Measure(new Size(availableWidth, double.PositiveInfinity));
        return surface.DesiredSize.Height;
    }

    private static void SetGroupContentState(Border content, bool isExpanded)
    {
        content.IsVisible = isExpanded;
        content.IsHitTestVisible = isExpanded;
        content.MaxHeight = isExpanded ? double.PositiveInfinity : 0;
        content.Opacity = isExpanded ? 1 : 0;
    }

    private void RemoveGroupContentAnimation(Border content)
    {
        if (!_groupContentAnimations.Remove(content, out var state))
        {
            return;
        }

        state.Card.PropertyChanged -= state.Handler;
    }

    private sealed class GroupContentAnimationState(
        ProxyGroupCardViewModel card,
        PropertyChangedEventHandler handler)
    {
        public ProxyGroupCardViewModel Card { get; } = card;

        public PropertyChangedEventHandler Handler { get; } = handler;

    }
}
