namespace Stelliberty.Application.Settings;

// 窗口按钮来源：系统标题栏、应用自绘（Windows / macOS 风格）或无边框。
public enum WindowDecorationStyle
{
    System,
    Windows,
    MacOS,
    None
}

// Windows / macOS 提供本平台与对方的自绘风格加无边框；Linux 三种装饰加无边框都可选。
// 缺失或不支持的值回退到平台默认。
public static class WindowDecorationStyleRules
{
    public static WindowDecorationStyle Default =>
        OperatingSystem.IsWindows() ? WindowDecorationStyle.Windows
        : OperatingSystem.IsMacOS() ? WindowDecorationStyle.MacOS
        : WindowDecorationStyle.System;

    public static string DefaultValue => Default.ToString();

    public static IReadOnlyList<WindowDecorationStyle> Supported =>
        OperatingSystem.IsWindows()
            ? [WindowDecorationStyle.Windows, WindowDecorationStyle.MacOS, WindowDecorationStyle.None]
            : OperatingSystem.IsMacOS()
                ? [WindowDecorationStyle.MacOS, WindowDecorationStyle.Windows, WindowDecorationStyle.None]
                : [WindowDecorationStyle.System, WindowDecorationStyle.Windows, WindowDecorationStyle.MacOS, WindowDecorationStyle.None];

    public static WindowDecorationStyle Parse(string? value)
    {
        return Enum.TryParse<WindowDecorationStyle>(value, ignoreCase: true, out var style)
               && Supported.Contains(style)
            ? style
            : Default;
    }
}
