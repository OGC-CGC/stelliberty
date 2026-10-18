namespace Stelliberty.Presentation.ViewModels;

// Emphasis: mono 等宽；accent 强调；muted 次要
public sealed class ConnectionDetailRowViewModel(
    string label,
    string value,
    string emphasis = "",
    bool canCopy = false) : ViewModelBase
{
    private string _value = value;

    public string Label { get; } = label;

    public string Value => _value;

    public string Emphasis { get; } = emphasis;

    public bool CanCopy { get; } = canCopy;

    public bool UpdateValue(string value) => SetProperty(ref _value, value, nameof(Value));
}
