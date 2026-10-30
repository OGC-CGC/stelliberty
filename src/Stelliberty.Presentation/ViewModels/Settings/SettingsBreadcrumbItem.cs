using System.Windows.Input;

namespace Stelliberty.Presentation.ViewModels;

public sealed class SettingsBreadcrumbItem
{
    public SettingsBreadcrumbItem(string text, ICommand? command, bool showSeparator, bool showIcon = false)
    {
        Text = text;
        Command = command;
        ShowSeparator = showSeparator;
        ShowIcon = showIcon;
    }

    public string Text { get; }

    public ICommand? Command { get; }

    public bool ShowSeparator { get; }

    /// <summary>是否在文字前显示设置图标，仅根级“设置”面包屑使用。</summary>
    public bool ShowIcon { get; }

    public bool CanNavigate => Command is not null;
}
