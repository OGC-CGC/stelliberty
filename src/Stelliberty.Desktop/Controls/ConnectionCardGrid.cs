using Avalonia;
using Avalonia.Controls;

namespace Stelliberty.Desktop.Controls;

public sealed class ConnectionCardGrid : Grid
{
    public static readonly StyledProperty<bool> IsListLayoutProperty =
        AvaloniaProperty.Register<ConnectionCardGrid, bool>(nameof(IsListLayout));

    public ConnectionCardGrid() => UpdateLayoutDefinitions();

    public bool IsListLayout
    {
        get => GetValue(IsListLayoutProperty);
        set => SetValue(IsListLayoutProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsListLayoutProperty)
        {
            UpdateLayoutDefinitions();
        }
    }

    private void UpdateLayoutDefinitions()
    {
        ColumnDefinitions = new ColumnDefinitions(IsListLayout ? "*,300" : "*");
        RowDefinitions = new RowDefinitions(IsListLayout ? "Auto,*" : "Auto,*,Auto");
        ColumnSpacing = IsListLayout ? 16 : 0;
        RowSpacing = IsListLayout ? 10 : 12;
    }
}
