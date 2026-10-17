using Stelliberty.Application.Localization;
using Stelliberty.Application.Platform;

namespace Stelliberty.Presentation.ViewModels;

public sealed class NetworkDiagnosticItemViewModel(
    NetworkDiagnosticTarget target,
    string name,
    string protocol,
    ILocalizationService? localization,
    string? url = null,
    string? id = null,
    NetworkDiagnosticTestType testType = NetworkDiagnosticTestType.Http) : ViewModelBase
{
    private NetworkDiagnosticResult? _result;
    private bool _isTesting;

    public NetworkDiagnosticTarget Target { get; } = target;
    public string Name { get; } = name;
    public string Protocol { get; } = protocol;
    public string? Url { get; } = url;
    public NetworkDiagnosticTestType TestType { get; } = testType;
    public string ProtocolDescription => TestType == NetworkDiagnosticTestType.Ssh
        ? Localize("Home.Connectivity.SshDescription")
        : Protocol;
    public string AutomationId => $"Home.Connectivity.{id ?? Target.ToString()}";
    public bool IsSuccessful => !_isTesting && _result?.Status == NetworkDiagnosticStatus.Success;
    public bool IsFailed => !_isTesting && _result is { Status: not NetworkDiagnosticStatus.Success };
    public string? Error => _result?.Error;

    public string ResultText => _isTesting
        ? Localize("Home.Connectivity.Testing")
        : _result switch
        {
            { Status: NetworkDiagnosticStatus.Success, PublicIp: { } ip } => $"{CountryLabel} · {ip}",
            { Status: NetworkDiagnosticStatus.Success, LatencyMilliseconds: { } latency } => $"{latency:0} ms",
            { Status: NetworkDiagnosticStatus.Timeout } => Localize("Home.Connectivity.Timeout"),
            { Status: NetworkDiagnosticStatus.Failed } => Localize("Home.Connectivity.Failed"),
            _ => Localize("Home.Connectivity.NotTested")
        };

    private string CountryLabel
    {
        get
        {
            var code = _result?.CountryCode?.ToUpperInvariant();
            if (code is not { Length: 2 } || code is "XX" or "ZZ"
                || !code.All(character => character is >= 'A' and <= 'Z'))
            {
                return "🌐";
            }

            var flag = string.Concat(code.Select(character => char.ConvertFromUtf32(0x1F1E6 + character - 'A')));
            return $"{flag} {code}";
        }
    }

    public void BeginTest()
    {
        _isTesting = true;
        _result = null;
        RefreshPresentation();
    }

    public void CompleteTest(NetworkDiagnosticResult result)
    {
        _isTesting = false;
        _result = result;
        RefreshPresentation();
    }

    public void RefreshPresentation()
    {
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(IsSuccessful));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(ProtocolDescription));
    }

    private string Localize(string key) => localization?.GetString(key) ?? key;
}
