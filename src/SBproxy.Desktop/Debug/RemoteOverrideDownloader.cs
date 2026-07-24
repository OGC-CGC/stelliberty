using SBproxy.Application.Overrides;
using SBproxy.Domain.Overrides;
using SBproxy.Infrastructure.Overrides;

namespace SBproxy.Desktop.Debug;

#if DEBUG
internal sealed class RemoteOverrideDownloader : IRemoteOverrideDownloader
{
    public Task<string> DownloadAsync(OverrideProfile overrideProfile, CancellationToken cancellationToken = default)
    {
        return new HttpRemoteOverrideDownloader().DownloadAsync(overrideProfile, cancellationToken);
    }
}
#endif
