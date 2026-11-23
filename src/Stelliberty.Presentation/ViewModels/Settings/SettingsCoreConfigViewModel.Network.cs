using System.Windows.Input;
using Stelliberty.Application.Settings;

namespace Stelliberty.Presentation.ViewModels;

public sealed partial class SettingsCoreConfigViewModel
{
    public string NetworkDelaySectionText => _localization.GetString("Settings.Network.Section.Delay");
    public string NetworkLanSectionText => _localization.GetString("Settings.Network.Section.Lan");
    public string NetworkConnectionSectionText => _localization.GetString("Settings.Network.Section.Connection");
    public string NetworkUnifiedDelayText => _localization.GetString("Settings.Network.UnifiedDelay");
    public string NetworkDelayUrlText => _localization.GetString("Settings.Network.DelayUrl");
    public string NetworkAllowLanText => _localization.GetString("Settings.Network.AllowLan");
    public string NetworkLanAuthText => _localization.GetString("Settings.Network.LanAuth");
    public string NetworkLanAuthUserNameText => _localization.GetString("Settings.Network.LanAuthUserName");
    public string NetworkLanAuthPasswordText => _localization.GetString("Settings.Network.LanAuthPassword");
    public string NetworkIpv6Text => _localization.GetString("Settings.Network.Ipv6");
    public string NetworkTcpConcurrentText => _localization.GetString("Settings.Network.TcpConcurrent");
    public string NetworkLanAllowedIpsText => _localization.GetString("Settings.Network.LanAllowedIps");
    public string NetworkLanDisallowedIpsText => _localization.GetString("Settings.Network.LanDisallowedIps");
    public string NetworkSkipAuthPrefixesText => _localization.GetString("Settings.Network.SkipAuthPrefixes");
    public string OutboundInterfaceText => _localization.GetString("Settings.Network.OutboundInterface");
    public string OutboundInterfaceListText => _localization.GetString($"Settings.Network.OutboundInterface.{_settings.OutboundInterfaceMode}");

    private readonly Func<IReadOnlyList<string>> _outboundInterfaceNames;

    public ICommand ToggleOutboundInterfaceCommand { get; }

    public IReadOnlyList<SelectionOption<OutboundInterfaceMode>> OutboundInterfaceModes => Enum.GetValues<OutboundInterfaceMode>()
        .Select(mode => new SelectionOption<OutboundInterfaceMode>(mode, _localization.GetString($"Settings.Network.OutboundInterface.{mode}")))
        .ToArray();

    public SelectionOption<OutboundInterfaceMode> SelectedOutboundInterfaceMode
    {
        get => OutboundInterfaceModes.FirstOrDefault(mode => mode.Value == _settings.OutboundInterfaceMode) ?? OutboundInterfaceModes[0];
        set
        {
            if (value is not null && SetLocalWithArea(_settings.OutboundInterfaceMode, value.Value,
                mode => _settings.OutboundInterfaceMode = mode, "Network"))
            {
                OnPropertyChanged(nameof(HasOutboundInterfaceList));
                OnPropertyChanged(nameof(OutboundInterfaceListText));
                OnPropertyChanged(nameof(OutboundInterfaceChoices));
            }
        }
    }

    public bool HasOutboundInterfaceList => _settings.OutboundInterfaceMode is OutboundInterfaceMode.Blacklist or OutboundInterfaceMode.Whitelist;

    private List<string> SelectedOutboundInterfaces => _settings.OutboundInterfaceMode == OutboundInterfaceMode.Whitelist
        ? _settings.OutboundInterfaceWhitelist
        : _settings.OutboundInterfaceBlacklist;

    public IReadOnlyList<OutboundInterfaceChoice> OutboundInterfaceChoices => !HasOutboundInterfaceList ? [] : _outboundInterfaceNames()
        .Union(SelectedOutboundInterfaces, StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Select(name => new OutboundInterfaceChoice(name, SelectedOutboundInterfaces.Contains(name, StringComparer.Ordinal)))
        .ToArray();

    public void RefreshOutboundInterfaceChoices() => OnPropertyChanged(nameof(OutboundInterfaceChoices));

    private void ToggleOutboundInterface(string? name)
    {
        if (name is null || !HasOutboundInterfaceList)
        {
            return;
        }

        var selection = SelectedOutboundInterfaces;
        if (!selection.Remove(name))
        {
            selection.Add(name);
        }

        _settingsStore.Save(_settings);
        RecordChangeArea("Network");
        OnPropertyChanged(nameof(OutboundInterfaceChoices));
    }

    public IReadOnlyList<string> NetworkItems =>
    [
        NetworkUnifiedDelayText,
        NetworkDelayUrlText,
        NetworkLanAuthText,
        NetworkLanAllowedIpsText,
        NetworkLanDisallowedIpsText,
        NetworkSkipAuthPrefixesText,
        OutboundInterfaceText,
        NetworkIpv6Text,
        NetworkTcpConcurrentText
    ];

    public bool IsUnifiedDelayEnabled
    {
        get => _settings.IsUnifiedDelayEnabled;
        set => SetWithArea(_settings.IsUnifiedDelayEnabled, value, next => _settings.IsUnifiedDelayEnabled = next, "Network");
    }

    public string DelayTestUrl
    {
        get => _settings.DelayTestUrl;
        set => SetDelayTestUrl(value);
    }

    public bool IsAllowLanEnabled
    {
        get => _settings.IsAllowLanEnabled;
        set => SetWithArea(_settings.IsAllowLanEnabled, value, next => _settings.IsAllowLanEnabled = next, "Network");
    }

    public string LanAuthenticationUserName
    {
        get => _settings.LanAuthenticationUserName;
        set => SetWithArea(_settings.LanAuthenticationUserName, value, next => _settings.LanAuthenticationUserName = next, "Network");
    }

    public string LanAuthenticationPassword
    {
        get => _settings.LanAuthenticationPassword;
        set => SetWithArea(_settings.LanAuthenticationPassword, value, next => _settings.LanAuthenticationPassword = next, "Network");
    }

    public string LanAllowedIpsText
    {
        get => string.Join(Environment.NewLine, _settings.LanAllowedIps);
        set => SetStringListWithArea(_settings.LanAllowedIps, value, next => _settings.LanAllowedIps = next, "Network");
    }

    public string LanDisallowedIpsText
    {
        get => string.Join(Environment.NewLine, _settings.LanDisallowedIps);
        set => SetStringListWithArea(_settings.LanDisallowedIps, value, next => _settings.LanDisallowedIps = next, "Network");
    }

    public string SkipAuthPrefixesText
    {
        get => string.Join(Environment.NewLine, _settings.SkipAuthPrefixes);
        set => SetStringListWithArea(_settings.SkipAuthPrefixes, value, next => _settings.SkipAuthPrefixes = next, "Network");
    }

    public bool IsIpv6Enabled
    {
        get => _settings.IsIpv6Enabled;
        set => SetWithArea(_settings.IsIpv6Enabled, value, next => _settings.IsIpv6Enabled = next, "Network");
    }

    public bool IsTcpConcurrentEnabled
    {
        get => _settings.IsTcpConcurrentEnabled;
        set => SetWithArea(_settings.IsTcpConcurrentEnabled, value, next => _settings.IsTcpConcurrentEnabled = next, "Network");
    }

    private void SetDelayTestUrl(string value)
    {
        var normalizedValue = value.Trim();
        if (!Uri.TryCreate(normalizedValue, UriKind.Absolute, out var uri) || !IsHttpDelayTestUri(uri))
        {
            return;
        }

        SetLocalWithArea(_settings.DelayTestUrl, normalizedValue, next => _settings.DelayTestUrl = next, "Network");
    }

    private static bool IsHttpDelayTestUri(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record OutboundInterfaceChoice(string Name, bool IsSelected);
