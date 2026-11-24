using Stelliberty.Application.Proxies;
using Stelliberty.Domain.Proxies;
using Stelliberty.Presentation.Proxies;

namespace Stelliberty.Presentation.ViewModels;

public sealed class ProxyNodeRowViewModel : ViewModelBase
{
    private ProxyNode _node;
    private bool _isSelected;
    private bool _isLocated;
    private bool _isClickable;
    private bool _isDelayTesting;
    private bool _isDelayQueued;
    private bool _isProxyGroup;

    public ProxyNodeRowViewModel(
        ProxyNode node,
        bool isSelected,
        bool isLocated = false,
        bool isClickable = true,
        bool isDelayTesting = false,
        bool isProxyGroup = false,
        bool isDelayQueued = false)
    {
        _node = node;
        _isSelected = isSelected;
        _isLocated = isLocated;
        _isClickable = isClickable;
        _isDelayTesting = isDelayTesting;
        _isDelayQueued = isDelayQueued;
        _isProxyGroup = isProxyGroup;
    }

    public string Name => _node.Name;
    public string RowAutomationId => $"Proxy.Node.{Name}";
    public string DelayAutomationId => $"Proxy.Node.{Name}.DelayText";
    public string TestDelayAutomationId => $"Proxy.Node.{Name}.TestDelayButton";
    public string DelayTestingAutomationId => $"Proxy.Node.{Name}.DelaySpinner";
    public string DelayQueuedAutomationId => $"Proxy.Node.{Name}.DelayQueued";
    public string SelectAutomationId => $"Proxy.Node.{Name}.SelectButton";

    public string Type => _node.Type;
    public string ActionAutomationId => $"Proxy.Node.{Name}.ActionButton";

    public bool IsProxyGroup
    {
        get => _isProxyGroup;
        private set => SetProperty(ref _isProxyGroup, value);
    }

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

    public bool IsDelayTesting => _isDelayTesting;
    public bool IsDelayQueued => _isDelayQueued;
    public bool ShowsDelayText => !IsDelayTesting && !IsDelayQueued;
    public bool IsDelayStatusCentered => !ShowsDelayText || _node.Delay is null;

    public string DelayText => _node.Delay switch
    {
        null => "—",
        < 0 => "-1 ms",
        _ => $"{_node.Delay} ms"
    };

    public string DelayState => (IsDelayTesting, IsDelayQueued, _node.Delay) switch
    {
        (true, _, _) => "testing",
        (_, true, _) => "queued",
        (_, _, null) => "untested",
        (_, _, < 0) => "failed",
        _ => "tested"
    };

    public string DelayLevel => ProxyDelayPresentation.GetLevel(_node.Delay);

    public void Update(
        ProxyNode node,
        bool isSelected,
        bool isLocated,
        bool isClickable,
        bool isDelayTesting,
        bool isProxyGroup = false,
        bool isDelayQueued = false)
    {
        if (!ReferenceEquals(_node, node))
        {
            var delayChanged = _node.Delay != node.Delay;
            _node = node;
            if (delayChanged)
            {
                NotifyDelayChanged();
            }
        }

        IsProxyGroup = isProxyGroup;
        IsSelected = isSelected;
        IsLocated = isLocated;
        IsClickable = isClickable;
        SetDelayActivity(isDelayTesting, isDelayQueued);
    }

    public void ApplyDelay(int delay)
    {
        _node = _node with { Delay = delay };
        SetDelayActivity(isTesting: false, isQueued: false);
        NotifyDelayChanged();
    }

    private void NotifyDelayChanged()
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayState));
        OnPropertyChanged(nameof(DelayLevel));
        OnPropertyChanged(nameof(IsDelayStatusCentered));
    }

    internal void SetDelayActivity(bool isTesting, bool isQueued)
    {
        if (_isDelayTesting == isTesting && _isDelayQueued == isQueued)
        {
            return;
        }

        _isDelayTesting = isTesting;
        _isDelayQueued = isQueued;
        OnPropertyChanged(nameof(IsDelayTesting));
        OnPropertyChanged(nameof(IsDelayQueued));
        OnPropertyChanged(nameof(ShowsDelayText));
        OnPropertyChanged(nameof(IsDelayStatusCentered));
        OnPropertyChanged(nameof(DelayState));
    }
}
