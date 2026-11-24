using Stelliberty.Application.Rules;
using Stelliberty.Domain.Rules;

namespace Stelliberty.Presentation.ViewModels;

public sealed class RuleEditorRowViewModel : ViewModelBase
{
    private bool _isEnabled;
    private bool? _pendingEnabled;
    private int _sequenceNumber;

    public RuleEditorRowViewModel(RuleEditorItem item)
    {
        Item = item;
        _isEnabled = item.IsEnabled;
    }

    public RuleEditorItem Item { get; }
    public event EventHandler? StateChanged;
    internal event EventHandler<bool>? ToggleRequested;

    public string Id => Item.Id;
    public string OrderId => Item.OrderId;
    public string Type => Item.Type;
    public string Payload => string.IsNullOrWhiteSpace(Item.Payload) ? "-" : Item.Payload;
    public string Proxy => Item.Proxy;
    public string Options => Item.Options;
    public string Source => Item.Source;
    public bool IsBuiltIn => Item.IsBuiltIn;
    public string SourceTag => IsBuiltIn ? "subscription" : "custom";
    public string RuleText => Item.ToEditableRule().Render();
    public string AutomationId => $"Rules.Editor.{(IsBuiltIn ? "Builtin" : "Custom")}.{Id}";
    public string ToggleAutomationId => $"{AutomationId}.Toggle";
    public string EditAutomationId => $"{AutomationId}.Edit";
    public string DeleteAutomationId => $"{AutomationId}.Delete";
    public string DuplicateText => Item.RuleCount > 1 ? $"x{Item.RuleCount}" : string.Empty;
    public int SequenceNumber
    {
        get => _sequenceNumber;
        set => SetProperty(ref _sequenceNumber, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!CanToggle || value == _isEnabled)
            {
                return;
            }

            if (ToggleRequested is { } requestToggle)
            {
                requestToggle(this, value);
                return;
            }

            CommitToggle(value);
        }
    }

    public bool? ToggleState
    {
        get => CanToggle ? IsEnabled : null;
        set => IsEnabled = value ?? !IsEnabled;
    }

    public bool CanToggle => _pendingEnabled is null;
    public bool ToggleTarget => _pendingEnabled ?? IsEnabled;

    internal void SetPendingEnabled(bool? target)
    {
        _pendingEnabled = target;
        OnPropertyChanged(nameof(ToggleTarget));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ToggleState));
    }

    internal void CommitToggle(bool enabled)
    {
        if (SyncEnabled(enabled))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal bool SyncEnabled(bool enabled)
    {
        if (!SetProperty(ref _isEnabled, enabled, nameof(IsEnabled)))
        {
            return false;
        }

        OnPropertyChanged(nameof(ToggleTarget));
        OnPropertyChanged(nameof(ToggleState));
        return true;
    }

    public EditableRule ToEditableRule() => Item.ToEditableRule() with { IsEnabled = IsEnabled };
}
