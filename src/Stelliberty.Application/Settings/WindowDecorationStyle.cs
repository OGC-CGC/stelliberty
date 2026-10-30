namespace Stelliberty.Application.Settings;

// 窗口按钮来源：系统标题栏、应用自绘（Windows / macOS 风格）或无边框。
public enum WindowDecorationStyle
{
    System,
    Windows,
    MacOS,
    None
}

// Windows 不提供系统装饰选项；缺失或不支持的值回退到平台默认。
public static class WindowDecorationStyleRules
{
    public static WindowDecorationStyle Default =>
        OperatingSystem.IsWindows() ? WindowDecorationStyle.Windows : WindowDecorationStyle.System;

    public static string DefaultValue => Default.ToString();

    public static IReadOnlyList<WindowDecorationStyle> Supported =>
        OperatingSystem.IsWindows()
            ? [WindowDecorationStyle.Windows, WindowDecorationStyle.MacOS, WindowDecorationStyle.None]
            : [WindowDecorationStyle.System, WindowDecorationStyle.Windows, WindowDecorationStyle.MacOS, WindowDecorationStyle.None];

    public static WindowDecorationStyle Parse(string? value)
    {
        return Enum.TryParse<WindowDecorationStyle>(value, ignoreCase: true, out var style)
               && Supported.Contains(style)
            ? style
            : Default;
    }
}
