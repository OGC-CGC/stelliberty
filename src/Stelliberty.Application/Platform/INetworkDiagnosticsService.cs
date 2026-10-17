namespace Stelliberty.Application.Platform;

public enum NetworkDiagnosticTarget
{
    Ipinfo,
    CloudflareIp,
    Custom
}

public enum NetworkDiagnosticStatus
{
    Success,
    Timeout,
    Failed
}

public enum NetworkDiagnosticTestType
{
    Http,
    Tcp,
    Ssh
}

public sealed record NetworkDiagnosticResult(
    NetworkDiagnosticStatus Status,
    double? LatencyMilliseconds = null,
    string? PublicIp = null,
    string? Error = null,
    string? CountryCode = null);

public interface INetworkDiagnosticsService
{
    Task<NetworkDiagnosticResult> TestAsync(NetworkDiagnosticTarget target, CancellationToken cancellationToken,
        string? testUrl = null, NetworkDiagnosticTestType? testType = null);
}
