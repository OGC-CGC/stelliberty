using Stelliberty.Application.Platform;
using Stelliberty.Application.Updates;

namespace Stelliberty.Application.Settings;

public sealed class AppSettings
{

#if DEBUG
    public const int DefaultMixedPort = 2000;
    public const string DefaultExternalControllerAddress = "127.0.0.1:1999";
#else
    public const int DefaultMixedPort = 8888;
    public const string DefaultExternalControllerAddress = "127.0.0.1:9090";
#endif
    // macOS 留空让核心自动分配未占用的 utunN。
    public static string DefaultTunDevice => OperatingSystem.IsMacOS() ? string.Empty : "clash";
    public const int DefaultTunMtu = 9000;
    public const int DefaultLightweightModeShutdownDelaySeconds = 60;
    public const int MinLightweightModeShutdownDelaySeconds = 0;
    public const int MaxLightweightModeShutdownDelaySeconds = 86400;

    public string Language { get; set; } = "System";

    public string Theme { get; set; } = "System";

    public const string DefaultBackgroundImagePath = "builtin:default";

    public string BackgroundImagePath { get; set; } = DefaultBackgroundImagePath;

    public string BackgroundImageStretch { get; set; } = "None";

    public string BackgroundImageHorizontalAlignment { get; set; } = "Right";

    public string BackgroundImageVerticalAlignment { get; set; } = "Bottom";

    public int BackgroundImageTransparency { get; set; } = 67;

    public string AccentColorMode { get; set; } = "System";

    public string AccentColor { get; set; } = "#3B82F6";

    public string WindowEffect { get; set; } = "None";

    public string WindowDecorationStyle { get; set; } = WindowDecorationStyleRules.DefaultValue;

    public bool IsSidebarDividerVisible { get; set; } = true;

    public bool IsSidebarCollapsed { get; set; } = true;

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool IsWindowMaximized { get; set; }

    public bool IsSilentStartEnabled { get; set; }

    public bool IsMinimizeToTrayEnabled { get; set; } = true;

    public bool IsCliEnabled { get; set; }

    public string? CliPathProfileEntry { get; set; }

    public bool IsLightweightModeEnabled { get; set; }

    public int LightweightModeShutdownDelaySeconds { get; set; } = DefaultLightweightModeShutdownDelaySeconds;

    public bool IsTrayDoubleClickEnabled { get; set; } = true;

    public bool IsLazyModeEnabled { get; set; }

    public bool IsTitleBarFpsVisible { get; set; }

    public bool IsAutoStartEnabled { get; set; }

    public string WindowToggleHotkey { get; set; } = string.Empty;

    public string SystemProxyToggleHotkey { get; set; } = string.Empty;

    public string TunToggleHotkey { get; set; } = string.Empty;

    public bool IsAutoCheckUpdateEnabled { get; set; }

    public string AppUpdateCheckInterval { get; set; } = "startup";

    public string AppUpdateChannel { get; set; } = "stable";

    public bool IsCustomAppUpdateSourceEnabled { get; set; }

    public string CustomAppUpdateRepositoryUrl { get; set; } = GitHubUpdateRepository.DefaultRepositoryUrl;

    public string GetAppUpdateRepositoryUrl() => IsCustomAppUpdateSourceEnabled
        ? CustomAppUpdateRepositoryUrl
        : GitHubUpdateRepository.DefaultRepositoryUrl;

    public string IgnoredUpdateVersion { get; set; } = string.Empty;

    public bool IsWebDavBackupEnabled { get; set; }

    public string WebDavUrl { get; set; } = string.Empty;

    public string WebDavUserName { get; set; } = string.Empty;

    public string WebDavPassword { get; set; } = string.Empty;

    public string WebDavRemoteDirectory { get; set; } = "stelliberty-backups";

    public int WebDavBackupIntervalHours { get; set; } = 24;

    public int WebDavBackupRetentionCount { get; set; } = 5;

    public DateTimeOffset? LastWebDavBackupTime { get; set; }

    public string LastCoreVersion { get; set; } = string.Empty;

    public DateTimeOffset? LastAppUpdateCheckTime { get; set; }

    public bool IsUnifiedDelayEnabled { get; set; }

    public string OutboundMode { get; set; } = "Rule";

    public string ProxyPageLayout { get; set; } = "Horizontal";

    public string ProxyNodeSortMode { get; set; } = "Default";

    public string ConnectionSortMode { get; set; } = "Default";

    public string ConnectionPageLayout { get; set; } = "Grid";

    public bool IsConnectionSortDescending { get; set; }

    public string DelayTestUrl { get; set; } = "https://www.gstatic.com/generate_204";

    // 旧配置未包含此字段时使用默认项；空列表表示用户主动清空。
    public IReadOnlyList<NetworkDiagnosticCheck>? ConnectivityChecks { get; set; }

    public bool IsAllowLanEnabled { get; set; }

    public string LanAuthenticationUserName { get; set; } = string.Empty;

    public string LanAuthenticationPassword { get; set; } = string.Empty;

    public IReadOnlyList<string> LanAllowedIps { get; set; } = [];

    public IReadOnlyList<string> LanDisallowedIps { get; set; } = [];

    public IReadOnlyList<string> SkipAuthPrefixes { get; set; } = [];

    public bool IsIpv6Enabled { get; set; }

    public bool IsTcpConcurrentEnabled { get; set; }

    public int MixedPort { get; set; } = DefaultMixedPort;

    public int? SocksPort { get; set; }

    public int? HttpPort { get; set; }

    public int? RedirPort { get; set; }

    public bool IsExternalControllerEnabled { get; set; }

    public string ExternalControllerAddress { get; set; } = DefaultExternalControllerAddress;

    public string ExternalControllerSecret { get; set; } = string.Empty;

    public string ProxyHost { get; set; } = "127.0.0.1";

    public string SystemProxyBypass { get; set; } = string.Empty;

    public bool IsPacModeEnabled { get; set; }

    public string PacScript { get; set; } = string.Empty;

    public bool IsTunEnabled { get; set; }

    public string TunStack { get; set; } = "Mixed";

    public string TunDevice { get; set; } = DefaultTunDevice;

    public bool IsTunAutoRouteEnabled { get; set; } = true;

    public bool IsTunAutoRedirectEnabled { get; set; } = true;

    public bool IsTunAutoDetectInterfaceEnabled { get; set; } = true;

    public bool IsTunStrictRouteEnabled { get; set; } = true;

    public IReadOnlyList<string> TunDnsHijack { get; set; } = ["any:53"];

    public IReadOnlyList<string> TunRouteExcludeAddresses { get; set; } = [];

    public bool IsTunIcmpForwardingDisabled { get; set; }

    public int? TunMtu { get; set; } = DefaultTunMtu;

    public bool IsDnsOverrideEnabled { get; set; }

    public bool IsDnsEnabled { get; set; } = true;

    public string DnsListen { get; set; } = ":53";

    public string DnsEnhancedMode { get; set; } = DnsDefaults.EnhancedMode;

    public string FakeIpRange { get; set; } = DnsDefaults.FakeIpRange;

    public bool IsDnsRespectRulesEnabled { get; set; }

    public IReadOnlyList<string> NameServers { get; set; } = DnsDefaults.NameServers;

    public IReadOnlyList<string> FallbackNameServers { get; set; } = [];

    public IReadOnlyList<string> ProxyServerNameServers { get; set; } = DnsDefaults.ProxyServerNameServers;

    public IReadOnlyList<string> DefaultNameServers { get; set; } = DnsDefaults.DefaultNameServers;

    public IReadOnlyList<string> FakeIpFilters { get; set; } = DnsDefaults.FakeIpFilters;

    public string FallbackFilterGeoIpCode { get; set; } = DnsDefaults.FallbackFilterGeoIpCode;

    public IReadOnlyList<string> Hosts { get; set; } = [];

    public bool IsDnsIpv6Enabled { get; set; } = true;

    public bool IsDnsUseHostsEnabled { get; set; }

    public bool IsDnsUseSystemHostsEnabled { get; set; }

    public IReadOnlyList<string> DirectNameServers { get; set; } = [];

    public IReadOnlyList<string> NameServerPolicy { get; set; } = [];

    public bool IsDnsPreferH3Enabled { get; set; }

    public string FakeIpFilterMode { get; set; } = DnsDefaults.FakeIpFilterMode;

    public bool IsDirectNameServerFollowPolicyEnabled { get; set; }

    public bool IsFallbackFilterGeoIpEnabled { get; set; } = true;

    public IReadOnlyList<string> FallbackFilterIpCidrs { get; set; } = DnsDefaults.FallbackFilterIpCidrs;

    public IReadOnlyList<string> FallbackFilterDomains { get; set; } = DnsDefaults.FallbackFilterDomains;

    public string GeoDataLoader { get; set; } = "standard";

    public string FindProcessMode { get; set; } = "off";

    public bool IsTcpKeepAliveEnabled { get; set; }

    public int TcpKeepAliveInterval { get; set; } = 30;

    public string CoreLogLevel { get; set; } = "silent";
}
