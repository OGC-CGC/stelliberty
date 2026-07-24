using SBproxy.Domain.Overrides;
namespace SBproxy.Application.Overrides;

public interface IRemoteOverrideDownloader
{
    Task<string> DownloadAsync(OverrideProfile overrideProfile, CancellationToken cancellationToken = default);
}
