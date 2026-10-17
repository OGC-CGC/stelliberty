namespace Stelliberty.Application.Platform;

public static class NetworkDiagnosticAddress
{
    public static bool IsHttpUrl(string address) => Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static bool TryParseSocketAddress(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (!Uri.TryCreate($"tcp://{address.Trim()}", UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535
            || uri.AbsolutePath is not ("" or "/")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        host = uri.DnsSafeHost;
        port = uri.Port;
        return true;
    }
}
