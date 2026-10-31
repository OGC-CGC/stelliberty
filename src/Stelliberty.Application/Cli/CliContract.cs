using System.Globalization;
using System.Text.Json;
using Stelliberty.Application.Settings;

namespace Stelliberty.Application.Cli;

public sealed record CliRequest(string[] Arguments);
public sealed record CliResponse(bool Success, string Status, object? Data = null, string? Error = null);

// 只有目录中声明的字段可读写；不反射导出整个用户配置。
public sealed record CliSetting(
    string Group, string Name, string Owner, string Property, string SettingProperty,
    string Type, string Description, string Example, string? Options = null,
    int? Minimum = null, int? Maximum = null, bool Sensitive = false)
{
    public object Schema => new
    {
        group = Group,
        name = Name,
        type = Type,
        description = Description,
        choices = Options?.Split('|'),
        minimum = Minimum,
        maximum = Maximum,
        default_value = Sensitive ? null : typeof(AppSettings).GetProperty(SettingProperty)?.GetValue(new AppSettings()),
        sensitive = Sensitive,
        effect = Group == "settings" ? "Saved and reflected in the UI; startup settings apply on next start."
            : "Saved; runtime regeneration is awaited. DNS values require dns override=true to override a subscription.",
        example = $"stelliberty-cli {Group} set {Name} {Example}"
    };

    public object Parse(string value)
    {
        if (Options is not null)
        {
            return Options.Split('|').FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{Name}: choose one of {Options}.");
        }
        if (Type == "boolean")
        {
            return bool.TryParse(value, out var flag) ? flag
                : throw new ArgumentException($"{Name}: expected true or false.");
        }
        if (Type == "integer")
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || (Minimum is { } min && number < min) || (Maximum is { } max && number > max))
            {
                throw new ArgumentException($"{Name}: expected an integer in {Minimum}..{Maximum}.");
            }
            return number;
        }
        if (Type == "lines")
        {
            var lines = JsonSerializer.Deserialize<string[]>(value)
                ?? throw new ArgumentException($"{Name}: expected a JSON array of strings.");
            if (lines.Any(line => line is null || line.Contains('\n') || line.Contains('\r')))
            {
                throw new ArgumentException($"{Name}: each array element must be a single line.");
            }
            return string.Join(Environment.NewLine, lines);
        }
        return value;
    }
}

public static class CliCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public const string Prerequisites = "Start the installed app and enable Settings > App behavior > Enable command-line control manually. The CLI cannot enable itself. Calls use current-user local IPC with the running background tray. Commands never start or activate the UI; they do not start the tray if it has exited. Windows, macOS and Linux are supported; window effects and TUN depend on platform capabilities.";
    public const string Results = "stdout: JSON {success,status,data,error}. Exit codes: 0 success/help, 2 invalid arguments, 3 disabled, 4 unavailable/version mismatch, 5 operation failed, 6 timeout (outcome unknown; read state before retrying). --dry-run validates without mutation. There is no automatic retry. Secret fields are redacted; subscription URLs are omitted.";

    public static CliSetting Find(string group, string name) => Settings.FirstOrDefault(item => item.Group == group && item.Name == name)
        ?? throw new ArgumentException($"Unknown setting '{name}'. Use '{group} schema'.");

    public static readonly CliSetting[] Settings =
    [
        new("settings", "language", "Language", "SelectedOption", "Language", "string", "Interface language.", "en", "System|ZhHans|ZhHant|En"),
        new("settings", "theme", "Theme", "SelectedOption", "Theme", "string", "Light, dark or system theme.", "dark", "System|Light|Dark"),
        new("settings", "accent-mode", "Theme", "SelectedAccentModeOption", "AccentColorMode", "string", "Use system or custom accent color.", "custom", "System|Custom"),
        new("settings", "accent-color", "Theme", "CustomAccentColor", "AccentColor", "string", "Custom accent in #RRGGBB format; also selects custom accent mode.", "\"#3B82F6\""),
        new("settings", "window-effect", "Theme", "SelectedWindowEffectOption", "WindowEffect", "string", "Window material; only effects supported on this platform can be selected.", "none", "None|Mica|Acrylic|Blur|FrutigerAero"),
        new("settings", "background-image", "Theme", "BackgroundImagePath", "BackgroundImagePath", "string", "Absolute readable image path, builtin:default, or an empty string to clear.", "\"/absolute/path/background.png\""),
        new("settings", "background-stretch", "Theme", "SelectedBackgroundStretchOption", "BackgroundImageStretch", "string", "Background scaling without changing the source image.", "uniform", "None|Uniform|UniformToFill|Fill"),
        new("settings", "background-horizontal", "Theme", "SelectedBackgroundHorizontalOption", "BackgroundImageHorizontalAlignment", "string", "Background horizontal alignment.", "right", "Left|Center|Right"),
        new("settings", "background-vertical", "Theme", "SelectedBackgroundVerticalOption", "BackgroundImageVerticalAlignment", "string", "Background vertical alignment.", "bottom", "Top|Center|Bottom"),
        new("settings", "background-transparency", "Theme", "BackgroundImageTransparency", "BackgroundImageTransparency", "integer", "0 is opaque; 100 is invisible.", "80", null, 0, 100),
        new("settings", "title-bar-fps", "AppBehavior", "IsTitleBarFpsVisible", "IsTitleBarFpsVisible", "boolean", "Show title-bar FPS.", "true"),
        new("settings", "minimize-to-tray", "AppBehavior", "IsMinimizeToTrayEnabled", "IsMinimizeToTrayEnabled", "boolean", "Keep the app in the tray when minimizing.", "true"),
        new("settings", "tray-double-click", "AppBehavior", "IsTrayDoubleClickEnabled", "IsTrayDoubleClickEnabled", "boolean", "Use double-click to activate the tray.", "true"),
        new("settings", "silent-start", "AppBehavior", "IsSilentStartEnabled", "IsSilentStartEnabled", "boolean", "Hide the UI on next application start.", "true"),
        new("settings", "lightweight-mode", "AppBehavior", "IsLightweightModeEnabled", "IsLightweightModeEnabled", "boolean", "Enable the existing lightweight UI lifecycle.", "true"),
        new("settings", "lightweight-delay", "AppBehavior", "LightweightModeShutdownDelaySecondsText", "LightweightModeShutdownDelaySeconds", "integer", "Delay in seconds before lightweight shutdown.", "60", null, 0, 86400),
        new("settings", "auto-check-update", "Update", "IsAutoCheckEnabled", "IsAutoCheckUpdateEnabled", "boolean", "Automatically check for application updates.", "true"),
        new("settings", "update-channel", "Update", "SelectedChannelOption", "AppUpdateChannel", "string", "Update channel used by the installed application.", "beta", "stable|beta"),
        new("settings", "update-interval", "Update", "SelectedCheckIntervalOption", "AppUpdateCheckInterval", "string", "Automatic update check interval.", "startup", "startup|1day|7days|14days"),
        new("settings", "auto-start", "AppBehavior", "IsAutoStartEnabled", "IsAutoStartEnabled", "boolean", "Register the app as a login item; disabling it removes the registration.", "true"),
        new("settings", "custom-update-source", "Update", "IsCustomUpdateSource", "IsCustomAppUpdateSourceEnabled", "boolean", "Check for updates from a custom GitHub repository.", "true"),
        new("settings", "custom-update-url", "Update", "CustomRepositoryUrl", "CustomAppUpdateRepositoryUrl", "string", "HTTPS GitHub repository used by the custom update source.", "\"https://github.com/owner/repo\""),
        new("settings", "webdav-backup", "DataManagement", "IsWebDavBackupEnabled", "IsWebDavBackupEnabled", "boolean", "Run scheduled backups to the configured WebDAV server.", "true"),
        new("settings", "webdav-url", "DataManagement", "WebDavUrl", "WebDavUrl", "string", "WebDAV endpoint URL including the collection path.", "\"https://dav.example.com/remote.php/dav/files/user\""),
        new("settings", "webdav-user", "DataManagement", "WebDavUserName", "WebDavUserName", "string", "WebDAV user name; use --stdin.", "--stdin", null, null, null, true),
        new("settings", "webdav-password", "DataManagement", "WebDavPassword", "WebDavPassword", "string", "WebDAV password; use --stdin.", "--stdin", null, null, null, true),
        new("settings", "webdav-directory", "DataManagement", "WebDavRemoteDirectory", "WebDavRemoteDirectory", "string", "Remote directory that receives backups.", "stelliberty-backups"),
        new("settings", "webdav-interval-hours", "DataManagement", "WebDavBackupIntervalHoursText", "WebDavBackupIntervalHours", "integer", "Hours between scheduled backups; the UI offers 1, 3, 6, 12 and 24.", "24", null, 1, 24),
        new("settings", "webdav-retention", "DataManagement", "WebDavBackupRetentionCountText", "WebDavBackupRetentionCount", "integer", "Number of remote backups to keep; the UI offers 1 to 5.", "5", null, 1, 5),
        new("proxy config", "mixed-port", "CoreConfig", "MixedPortText", "MixedPort", "integer", "HTTP/SOCKS mixed listener port.", "7890", null, 1, 65535),
        new("proxy config", "socks-port", "CoreConfig", "SocksPortText", "SocksPort", "integer", "SOCKS listener port; 0 disables it.", "7890", null, 0, 65535),
        new("proxy config", "http-port", "CoreConfig", "HttpPortText", "HttpPort", "integer", "HTTP listener port; 0 disables it.", "7890", null, 0, 65535),
        new("proxy config", "redir-port", "CoreConfig", "RedirPortText", "RedirPort", "integer", "Redirect listener; platform support required; 0 disables it.", "7890", null, 0, 65535),
        new("proxy config", "allow-lan", "CoreConfig", "IsAllowLanEnabled", "IsAllowLanEnabled", "boolean", "Allow connections from the LAN.", "true"),
        new("proxy config", "ipv6", "CoreConfig", "IsIpv6Enabled", "IsIpv6Enabled", "boolean", "Enable IPv6 in the core.", "true"),
        new("proxy config", "tcp-concurrent", "CoreConfig", "IsTcpConcurrentEnabled", "IsTcpConcurrentEnabled", "boolean", "Concurrent TCP connections.", "true"),
        new("proxy config", "unified-delay", "CoreConfig", "IsUnifiedDelayEnabled", "IsUnifiedDelayEnabled", "boolean", "Use unified delay measurement.", "true"),
        new("proxy config", "delay-test-url", "CoreConfig", "DelayTestUrl", "DelayTestUrl", "string", "HTTP/HTTPS URL used for delay tests.", "\"https://www.gstatic.com/generate_204\""),
        new("proxy config", "lan-user", "CoreConfig", "LanAuthenticationUserName", "LanAuthenticationUserName", "string", "LAN authentication username.", "--stdin", null, null, null, true),
        new("proxy config", "lan-password", "CoreConfig", "LanAuthenticationPassword", "LanAuthenticationPassword", "string", "LAN authentication password; use --stdin to avoid shell history.", "--stdin", null, null, null, true),
        new("proxy config", "lan-allowed-ips", "CoreConfig", "LanAllowedIpsText", "LanAllowedIps", "lines", "JSON string array of IP ranges, matching the settings UI.", "'[\"127.0.0.1/32\"]'"),
        new("proxy config", "lan-disallowed-ips", "CoreConfig", "LanDisallowedIpsText", "LanDisallowedIps", "lines", "JSON string array of IP ranges, matching the settings UI.", "'[\"127.0.0.1/32\"]'"),
        new("proxy config", "skip-auth-prefixes", "CoreConfig", "SkipAuthPrefixesText", "SkipAuthPrefixes", "lines", "JSON string array of IP ranges, matching the settings UI.", "'[\"127.0.0.1/32\"]'"),
        new("proxy config", "tun-stack", "CoreConfig", "TunStack", "TunStack", "string", "TUN stack; applied when TUN is enabled.", "mixed", "Mixed|System|gVisor|Mips"),
        new("proxy config", "tun-device", "CoreConfig", "TunDevice", "TunDevice", "string", "TUN device name; empty lets macOS allocate utun.", "\"\""),
        new("proxy config", "tun-mtu", "CoreConfig", "TunMtuText", "TunMtu", "integer", "TUN MTU.", "9000", null, 1280, 65535),
        new("proxy config", "tun-auto-route", "CoreConfig", "IsTunAutoRouteEnabled", "IsTunAutoRouteEnabled", "boolean", "Let the core configure TUN routes.", "true"),
        new("proxy config", "tun-auto-redirect", "CoreConfig", "IsTunAutoRedirectEnabled", "IsTunAutoRedirectEnabled", "boolean", "Automatic redirect; support depends on platform/core.", "true"),
        new("proxy config", "tun-auto-detect-interface", "CoreConfig", "IsTunAutoDetectInterfaceEnabled", "IsTunAutoDetectInterfaceEnabled", "boolean", "Detect the outbound interface.", "true"),
        new("proxy config", "tun-strict-route", "CoreConfig", "IsTunStrictRouteEnabled", "IsTunStrictRouteEnabled", "boolean", "Use strict TUN routes.", "true"),
        new("proxy config", "tun-disable-icmp-forwarding", "CoreConfig", "IsTunIcmpForwardingDisabled", "IsTunIcmpForwardingDisabled", "boolean", "Disable ICMP forwarding.", "true"),
        new("proxy config", "tun-dns-hijack", "CoreConfig", "TunDnsHijackText", "TunDnsHijack", "lines", "TUN DNS hijack targets. Supply a JSON string array.", "'[\"any:53\"]'"),
        new("proxy config", "tun-route-exclude", "CoreConfig", "TunRouteExcludeAddressesText", "TunRouteExcludeAddresses", "lines", "Routes excluded from TUN. Supply a JSON string array.", "'[\"192.168.0.0/16\"]'"),
        new("proxy config", "tun-enabled", "CoreConfig", "IsTunEnabled", "IsTunEnabled", "boolean", "Persistently enable TUN; requires the privileged service or administrator mode and applies while the core runs.", "true"),
        new("proxy config", "outbound-mode", "Home", "OutboundMode", "OutboundMode", "string", "Persisted routing mode written into the runtime config; 'proxy mode set' switches a running core immediately.", "rule", "Rule|Global|Direct"),
        new("proxy config", "proxy-host", "SystemIntegration", "ProxyHost", "ProxyHost", "string", "Host address written to system proxy settings.", "\"127.0.0.1\""),
        new("proxy config", "system-proxy-bypass", "SystemIntegration", "SystemProxyBypass", "SystemProxyBypass", "string", "Platform-specific bypass list written to system proxy settings; use --stdin for long values.", "--stdin"),
        new("proxy config", "pac-mode", "SystemIntegration", "IsPacModeEnabled", "IsPacModeEnabled", "boolean", "Configure system proxy through the PAC script instead of a fixed proxy.", "true"),
        new("proxy config", "pac-script", "SystemIntegration", "PacScript", "PacScript", "string", "PAC script used when pac-mode is on; empty restores the built-in script; use --stdin for multi-line scripts.", "--stdin"),
        new("proxy config", "external-controller", "PortControl", "IsExternalControllerEnabled", "IsExternalControllerEnabled", "boolean", "Expose the core external controller listener.", "true"),
        new("proxy config", "external-controller-address", "PortControl", "ExternalControllerAddress", "ExternalControllerAddress", "string", "Listen address of the external controller.", "\"127.0.0.1:9090\""),
        new("proxy config", "external-controller-secret", "PortControl", "ExternalControllerSecret", "ExternalControllerSecret", "string", "External controller secret; use --stdin.", "--stdin", null, null, null, true),
        new("proxy config", "geo-loader", "Performance", "GeoDataLoader", "GeoDataLoader", "string", "GeoIP/GeoSite loader strategy.", "memconservative", "standard|memconservative"),
        new("proxy config", "find-process-mode", "Performance", "FindProcessMode", "FindProcessMode", "string", "Process lookup mode used by rules.", "strict", "off|strict|always"),
        new("proxy config", "tcp-keepalive", "Performance", "IsTcpKeepAliveEnabled", "IsTcpKeepAliveEnabled", "boolean", "Enable TCP keep-alive for core connections.", "true"),
        new("proxy config", "tcp-keepalive-interval", "Performance", "TcpKeepAliveIntervalText", "TcpKeepAliveInterval", "integer", "TCP keep-alive interval in seconds.", "30", null, 1, 65535),
        new("proxy config", "core-log-level", "CoreLog", "SelectedCoreLogLevelOption", "CoreLogLevel", "string", "Core log verbosity.", "info", "silent|error|warning|info|debug"),
        new("dns", "override", "CoreConfig", "IsDnsOverrideEnabled", "IsDnsOverrideEnabled", "boolean", "Override subscription DNS settings with these values.", "true"),
        new("dns", "enabled", "CoreConfig", "IsDnsEnabled", "IsDnsEnabled", "boolean", "Enable the DNS listener.", "true"),
        new("dns", "respect-rules", "CoreConfig", "IsDnsRespectRulesEnabled", "IsDnsRespectRulesEnabled", "boolean", "Use routing rules for DNS connections.", "true"),
        new("dns", "ipv6", "CoreConfig", "IsDnsIpv6Enabled", "IsDnsIpv6Enabled", "boolean", "Resolve IPv6 records.", "true"),
        new("dns", "use-hosts", "CoreConfig", "IsDnsUseHostsEnabled", "IsDnsUseHostsEnabled", "boolean", "Use configured hosts.", "true"),
        new("dns", "use-system-hosts", "CoreConfig", "IsDnsUseSystemHostsEnabled", "IsDnsUseSystemHostsEnabled", "boolean", "Use operating-system hosts.", "true"),
        new("dns", "prefer-h3", "CoreConfig", "IsDnsPreferH3Enabled", "IsDnsPreferH3Enabled", "boolean", "Prefer HTTP/3 DNS.", "true"),
        new("dns", "direct-follow-policy", "CoreConfig", "IsDirectNameServerFollowPolicyEnabled", "IsDirectNameServerFollowPolicyEnabled", "boolean", "Apply nameserver policy to direct DNS.", "true"),
        new("dns", "fallback-geoip", "CoreConfig", "IsFallbackFilterGeoIpEnabled", "IsFallbackFilterGeoIpEnabled", "boolean", "Enable GeoIP fallback filtering.", "true"),
        new("dns", "listen", "CoreConfig", "DnsListen", "DnsListen", "string", "DNS listen address.", "\"127.0.0.1:1053\""),
        new("dns", "mode", "CoreConfig", "DnsEnhancedMode", "DnsEnhancedMode", "string", "DNS enhanced mode.", "fake-ip", "fake-ip|redir-host"),
        new("dns", "fake-ip-range", "CoreConfig", "FakeIpRange", "FakeIpRange", "string", "Fake-IP CIDR range.", "\"198.18.0.1/16\""),
        new("dns", "fake-ip-filter-mode", "CoreConfig", "FakeIpFilterMode", "FakeIpFilterMode", "string", "Fake-IP filter interpretation.", "blacklist", "blacklist|whitelist"),
        new("dns", "fallback-geoip-code", "CoreConfig", "FallbackFilterGeoIpCode", "FallbackFilterGeoIpCode", "string", "GeoIP country code.", "CN"),
        new("dns", "nameservers", "CoreConfig", "NameServersText", "NameServers", "lines", "Primary DNS servers. Supply a JSON string array.", "'[\"https://dns.google/dns-query\"]'"),
        new("dns", "fallback", "CoreConfig", "FallbackNameServersText", "FallbackNameServers", "lines", "Fallback DNS servers. Supply a JSON string array.", "'[\"1.1.1.1\"]'"),
        new("dns", "proxy-nameservers", "CoreConfig", "ProxyServerNameServersText", "ProxyServerNameServers", "lines", "DNS for proxy hostnames. Supply a JSON string array.", "'[\"1.1.1.1\"]'"),
        new("dns", "default-nameservers", "CoreConfig", "DefaultNameServersText", "DefaultNameServers", "lines", "Bootstrap DNS servers. Supply a JSON string array.", "'[\"1.1.1.1\"]'"),
        new("dns", "direct-nameservers", "CoreConfig", "DirectNameServersText", "DirectNameServers", "lines", "DNS for direct connections. Supply a JSON string array.", "'[\"1.1.1.1\"]'"),
        new("dns", "fake-ip-filters", "CoreConfig", "FakeIpFiltersText", "FakeIpFilters", "lines", "Fake-IP domain filters. Supply a JSON string array.", "'[\"*.lan\"]'"),
        new("dns", "hosts", "CoreConfig", "HostsText", "Hosts", "lines", "Host entries, using the same line format as the UI. Supply a JSON string array.", "'[]'"),
        new("dns", "nameserver-policy", "CoreConfig", "NameServerPolicyText", "NameServerPolicy", "lines", "Policy entries, using the same line format as the UI. Supply a JSON string array.", "'[]'"),
        new("dns", "fallback-cidrs", "CoreConfig", "FallbackFilterIpCidrsText", "FallbackFilterIpCidrs", "lines", "Fallback IP ranges. Supply a JSON string array.", "'[\"240.0.0.0/4\"]'"),
        new("dns", "fallback-domains", "CoreConfig", "FallbackFilterDomainsText", "FallbackFilterDomains", "lines", "Fallback domains. Supply a JSON string array.", "'[\"+.example.com\"]'"),
    ];
}
