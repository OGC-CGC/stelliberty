using SBproxy.Application.Proxies;
using SBproxy.Domain.Proxies;

namespace SBproxy.Presentation.ViewModels;

public sealed class ProxyNodeRowViewModel : ViewModelBase
{
    private ProxyNode _node;
    private bool _isSelected;
    private bool _isLocated;
    private bool _isClickable;
    private bool _isDelayTesting;
    private bool _isNode;
    private bool _isProxyGroup;
    private readonly IReadOnlyList<ProxyNodeMenuSelection> _menuOptions;

    public ProxyNodeRowViewModel(
        ProxyNode node,
        bool isSelected,
        bool isLocated = false,
        bool isClickable = true,
        bool isDelayTesting = false,
        bool isNode = true,
        bool isProxyGroup = false,
        IReadOnlyList<ProxyNodeMenuSelection>? menuOptions = null)
    {
        _node = node;
        _isSelected = isSelected;
        _isLocated = isLocated;
        _isClickable = isClickable;
        _isDelayTesting = isDelayTesting;
        _isNode = isNode;
        _isProxyGroup = isProxyGroup;
        _menuOptions = menuOptions ?? [];
    }

    public string Name => _node.Name;
    public string RowAutomationId => $"Proxy.Node.{Name}";
    public string DelayAutomationId => $"Proxy.Node.{Name}.DelayText";
    public string TestDelayAutomationId => $"Proxy.Node.{Name}.TestDelayButton";
    public string DelayTestingAutomationId => $"Proxy.Node.{Name}.DelaySpinner";
    public string SelectAutomationId => $"Proxy.Node.{Name}.SelectButton";
    public string ActionAutomationId => $"Proxy.Node.{Name}.ActionButton";

    public string Type => _node.Type;

    public bool IsNode => _isNode;

    public bool IsProxyGroup => _isProxyGroup;

    public bool HasAction => IsNode || IsProxyGroup;

    public string ActionIconType => IsNode ? "More3Line" : "SquareArrowRightLine";

    public IReadOnlyList<ProxyNodeMenuSelection> MenuOptions => IsNode ? _menuOptions : [];

    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public bool IsLocated
    {
        get => _isLocated;
        private set => SetProperty(ref _isLocated, value);
    }

    public bool IsClickable
    {
        get => _isClickable;
        private set => SetProperty(ref _isClickable, value);
    }

    public bool IsDelayTesting
    {
        get => _isDelayTesting;
        private set
        {
            if (SetProperty(ref _isDelayTesting, value))
            {
                OnPropertyChanged(nameof(DelayState));
            }
        }
    }

    public string DelayText => _node.Delay switch
    {
        null => "—",
        < 0 => "-1 ms",
        _ => $"{_node.Delay} ms"
    };

    public string DelayState => IsDelayTesting
        ? "testing"
        : _node.Delay switch
        {
            null => "untested",
            < 0 => "failed",
            _ => "tested"
        };

    public string DelayLevel => _node.Delay switch
    {
        null => "delay-none",
        < 0 => "delay-bad",
        <= 300 => "delay-good",
        <= 500 => "delay-mid",
        _ => "delay-slow"
    };

    public void Update(
        ProxyNode node,
        bool isSelected,
        bool isLocated,
        bool isClickable,
        bool isDelayTesting,
        bool isNode,
        bool isProxyGroup)
    {
        if (!ReferenceEquals(_node, node))
        {
            var delayChanged = _node.Delay != node.Delay;
            var typeChanged = !string.Equals(_node.Type, node.Type, StringComparison.Ordinal);
            _node = node;
            if (delayChanged)
            {
                OnPropertyChanged(nameof(DelayText));
                OnPropertyChanged(nameof(DelayState));
                OnPropertyChanged(nameof(DelayLevel));
            }

            if (typeChanged)
            {
                OnPropertyChanged(nameof(Type));
            }
        }

        IsSelected = isSelected;
        IsLocated = isLocated;
        IsClickable = isClickable;
        IsDelayTesting = isDelayTesting;
        SetEntryKind(isNode, isProxyGroup);
    }

    public void ApplyDelay(int delay)
    {
        _node = _node with { Delay = delay };
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayState));
        OnPropertyChanged(nameof(DelayLevel));
        IsDelayTesting = false;
    }

    private void SetEntryKind(bool isNode, bool isProxyGroup)
    {
        if (_isNode == isNode && _isProxyGroup == isProxyGroup)
        {
            return;
        }

        _isNode = isNode;
        _isProxyGroup = isProxyGroup;
        OnPropertyChanged(nameof(IsNode));
        OnPropertyChanged(nameof(IsProxyGroup));
        OnPropertyChanged(nameof(HasAction));
        OnPropertyChanged(nameof(ActionIconType));
        OnPropertyChanged(nameof(MenuOptions));
    }
}
