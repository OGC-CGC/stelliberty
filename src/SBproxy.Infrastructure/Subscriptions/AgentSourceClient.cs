using System.Text.Json;
using SBproxy.Application.Overrides;
using SBproxy.Application.Subscriptions;
using SBproxy.Domain.Overrides;
using SBproxy.Domain.Subscriptions;
using SBproxy.Infrastructure.Core;

namespace SBproxy.Infrastructure.Subscriptions;

public sealed class AgentSourceClient(string agentPipeName) :
    IRemoteOverrideDownloader,
    IRemoteSubscriptionDownloader,
    ILocalOverrideFileReader,
    ILocalSubscriptionFileReader,
    ISubscriptionProviderUploader,
    IDisposable
{
    private readonly JsonRpcPipeClient _client = new(agentPipeName);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> DownloadAsync(
        OverrideProfile overrideProfile,
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "sources.download",
            new
            {
                source_location = overrideProfile.SourceLocation,
                user_agent = string.Empty,
                proxy_mode = overrideProfile.UpdateProxyMode.ToString(),
            },
            cancellationToken).ConfigureAwait(false);
        return result.GetProperty("content").GetString() ?? string.Empty;
    }

    public async Task<RemoteSubscriptionDownloadResult> DownloadAsync(
        RemoteSubscriptionDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "sources.download",
            new
            {
                source_location = request.SourceLocation,
                user_agent = SubscriptionDefaults.NormalizeUserAgent(request.UserAgent),
                proxy_mode = request.ProxyMode.ToString(),
            },
            cancellationToken).ConfigureAwait(false);
        var content = result.GetProperty("content").GetString() ?? string.Empty;
        SubscriptionTrafficInfo? trafficInfo = null;
        if (result.TryGetProperty("traffic_info", out var traffic)
            && traffic.ValueKind == JsonValueKind.Object)
        {
            trafficInfo = new SubscriptionTrafficInfo(
                traffic.GetProperty("upload").GetInt64(),
                traffic.GetProperty("download").GetInt64(),
                traffic.GetProperty("total").GetInt64(),
                traffic.GetProperty("expire").GetInt64());
        }
        return new RemoteSubscriptionDownloadResult(content, trafficInfo);
    }

    string ILocalOverrideFileReader.ReadAllText(string filePath) => ReadLocal(filePath);

    string ILocalSubscriptionFileReader.ReadAllText(string filePath) => ReadLocal(filePath);

    public async Task<SubscriptionProviderUploadResult> UploadAsync(
        SubscriptionProvider provider,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "sources.provider.upload",
            new
            {
                vehicle_type = provider.VehicleType,
                provider_path = provider.Path,
                source_path = sourcePath,
            },
            cancellationToken).ConfigureAwait(false);
        return result.GetProperty("uploaded").GetBoolean()
            ? SubscriptionProviderUploadResult.Uploaded()
            : SubscriptionProviderUploadResult.Skipped(
                result.TryGetProperty("skip_reason", out var reason)
                    ? reason.GetString() ?? "upload skipped"
                    : "upload skipped");
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    private string ReadLocal(string path)
    {
        return RequestAsync(
                "sources.local.get",
                new { path },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            return await _client.RequestAsync(
                    method,
                    JsonSerializer.SerializeToElement(parameters),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
