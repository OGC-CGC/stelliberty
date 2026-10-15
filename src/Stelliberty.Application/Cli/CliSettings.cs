using System.Text.RegularExpressions;
using Stelliberty.Application.Settings;

namespace Stelliberty.Application.Cli;

// CLI 只访问目录中允许的持久设置，不依赖窗口属性或选择项。
public static class CliSettings
{
    public static object? Read(AppSettings settings, CliSetting field)
    {
        if (field.Sensitive || field.Group == "dns" && field.Type == "lines"
            || field.Name is "delay-test-url" or "background-image") return "<redacted>";
        var value = typeof(AppSettings).GetProperty(field.SettingProperty)!.GetValue(settings);
        if (field.Type == "integer" && value is null)
            return field.Name == "tun-mtu" ? AppSettings.DefaultTunMtu : 0;
        return value;
    }

    public static void Assign(AppSettings settings, CliSetting field, object parsed)
    {
        if (field.Name == "accent-color" && !Regex.IsMatch((string)parsed, "^#[0-9a-fA-F]{6}$"))
            throw new ArgumentException("accent-color requires #RRGGBB.");
        if (field.Name == "delay-test-url") ValidateHttpUrl((string)parsed);

        object? value = parsed;
        if (field.Type == "lines")
            value = ((string)parsed).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if ((field.Name is "socks-port" or "http-port" or "redir-port") && parsed is 0)
            value = null;
        if (field.Name is "tun-stack" or "delay-test-url" or "listen" or "fake-ip-range" or "fallback-geoip-code")
            value = ((string)parsed).Trim();
        typeof(AppSettings).GetProperty(field.SettingProperty)!.SetValue(settings, value);
        if (field.Name == "accent-color") settings.AccentColorMode = "Custom";
    }

    public static void ValidateHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Expected an absolute HTTP or HTTPS URL.");
    }
}
