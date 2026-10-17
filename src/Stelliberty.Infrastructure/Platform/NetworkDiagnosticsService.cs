using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Stelliberty.Application.Platform;

namespace Stelliberty.Infrastructure.Platform;

public sealed class NetworkDiagnosticsService : INetworkDiagnosticsService
{
    public async Task<NetworkDiagnosticResult> TestAsync(NetworkDiagnosticTarget target, CancellationToken cancellationToken,
        string? testUrl = null, NetworkDiagnosticTestType? testType = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        try
        {
            var type = testType ?? NetworkDiagnosticTestType.Http;
            if (type is NetworkDiagnosticTestType.Tcp or NetworkDiagnosticTestType.Ssh)
            {
                if (!NetworkDiagnosticAddress.TryParseSocketAddress(testUrl ?? string.Empty, out var host, out var port))
                {
                    return new(NetworkDiagnosticStatus.Failed, Error: "Invalid host:port address");
                }

                using var socket = new TcpClient();
                var started = Stopwatch.GetTimestamp();
                await socket.ConnectAsync(host, port, token).ConfigureAwait(false);
                if (type == NetworkDiagnosticTestType.Ssh)
                {
                    using var stream = socket.GetStream();
                    await stream.WriteAsync("SSH-2.0-Stelliberty_Connectivity\r\n"u8.ToArray(), token).ConfigureAwait(false);
                    if (!await ReadSshIdentificationAsync(stream, token).ConfigureAwait(false))
                    {
                        return new(NetworkDiagnosticStatus.Failed, Error: "SSH server identification missing or invalid");
                    }
                }

                return new(NetworkDiagnosticStatus.Success, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            var url = testUrl ?? (target switch
            {
                NetworkDiagnosticTarget.Ipinfo => "https://ipinfo.io/json",
                NetworkDiagnosticTarget.CloudflareIp => "https://www.cloudflare.com/cdn-cgi/trace",
                _ => string.Empty
            });
            if (!NetworkDiagnosticAddress.IsHttpUrl(url))
            {
                return new(NetworkDiagnosticStatus.Failed, Error: "Invalid HTTP(S) URL");
            }
            // 每轮使用新连接，HTTP(S) 遵循系统代理；TCP/SSH 探测遵循本机路由。
            using var handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Stelliberty/1.0");
            var requestStarted = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
            if (!response.IsSuccessStatusCode
                || url == "https://www.google.com/generate_204" && response.StatusCode != HttpStatusCode.NoContent)
            {
                return new(NetworkDiagnosticStatus.Failed, Error: $"HTTP {(int)response.StatusCode}");
            }

            if (target is NetworkDiagnosticTarget.Ipinfo or NetworkDiagnosticTarget.CloudflareIp)
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                string? ip;
                string? country;
                if (target == NetworkDiagnosticTarget.Ipinfo)
                {
                    using var json = JsonDocument.Parse(body);
                    ip = json.RootElement.TryGetProperty("ip", out var ipNode) ? ipNode.GetString() : null;
                    country = json.RootElement.TryGetProperty("country", out var countryNode) ? countryNode.GetString() : null;
                }
                else
                {
                    var lines = body.Split('\n');
                    ip = lines.FirstOrDefault(line => line.StartsWith("ip=", StringComparison.Ordinal))?[3..].Trim();
                    country = lines.FirstOrDefault(line => line.StartsWith("loc=", StringComparison.Ordinal))?[4..].Trim();
                }
                return IPAddress.TryParse(ip, out var address)
                    ? new(NetworkDiagnosticStatus.Success, PublicIp: address.ToString(), CountryCode: country)
                    : new(NetworkDiagnosticStatus.Failed, Error: "Invalid IP response");
            }

            return new(NetworkDiagnosticStatus.Success, latency);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(NetworkDiagnosticStatus.Timeout);
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException or JsonException)
        {
            return new(NetworkDiagnosticStatus.Failed, Error: exception.Message);
        }
    }

    private static async Task<bool> ReadSshIdentificationAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // RFC 4253：版本标识最多 255 字节（含 CRLF），允许标识前出现提示行。
        var buffer = new byte[255];
        while (true)
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                length += read;
                if (buffer[length - 1] == '\n')
                {
                    break;
                }
            }

            if (length < 2 || buffer[length - 1] != '\n' || buffer[length - 2] != '\r')
            {
                return false;
            }

            var line = Encoding.Latin1.GetString(buffer, 0, length - 2);
            if (!line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith("SSH-2.0-", StringComparison.Ordinal))
            {
                return false;
            }

            var softwareVersion = line[8..].Split(' ', 2)[0];
            return softwareVersion.Length > 0
                && softwareVersion.All(character => character is >= '!' and <= '~' && character != '-');
        }
    }
}
