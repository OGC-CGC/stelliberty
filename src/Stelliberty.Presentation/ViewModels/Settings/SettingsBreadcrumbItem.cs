using System.Windows.Input;

namespace Stelliberty.Presentation.ViewModels;

public sealed class SettingsBreadcrumbItem
{
    public SettingsBreadcrumbItem(string text, ICommand? command, bool showSeparator, SettingsSubPage page)
    {
        Text = text;
        Command = command;
        ShowSeparator = showSeparator;
        Page = page;
    }

    public string Text { get; }

    public ICommand? Command { get; }

    public bool ShowSeparator { get; }

    /// <summary>该级面包屑对应的设置页面，用于取图标。</summary>
    public SettingsSubPage Page { get; }

    public bool CanNavigate => Command is not null;
}
