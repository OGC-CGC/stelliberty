namespace Stelliberty.Presentation.ViewModels;

public sealed class OverrideSubscriptionOptionViewModel(string id, string name, bool isSelected) : ViewModelBase
{
    private bool _isSelected = isSelected;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string ToggleAutomationId => $"Overrides.EditDialog.Subscription.{Id}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
