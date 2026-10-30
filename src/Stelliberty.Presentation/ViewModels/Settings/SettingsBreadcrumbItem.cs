using System.Windows.Input;

namespace Stelliberty.Presentation.ViewModels;

public sealed class SettingsBreadcrumbItem
{
    public SettingsBreadcrumbItem(string text, ICommand? command, bool showSeparator)
    {
        Text = text;
        Command = command;
        ShowSeparator = showSeparator;
    }

    public string Text { get; }

    public ICommand? Command { get; }

    public bool ShowSeparator { get; }

    public bool CanNavigate => Command is not null;
}
