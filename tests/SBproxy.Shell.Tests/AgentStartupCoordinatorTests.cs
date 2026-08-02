using System.Text;
using SBproxy.Desktop;
using Xunit;

namespace SBproxy.Shell.Tests;

public sealed class AgentStartupCoordinatorTests
{
    [Fact(DisplayName = "Unix IPC endpoints use a short per-user directory")]
    public void UnixIpcEndpointsUseShortPerUserDirectory()
    {
        var directory = AgentStartupCoordinator.UnixIpcDirectory(501);
        var longestEndpoint = Path.Combine(directory, "sbproxy_mihomo_prod.sock");

        Assert.Equal("/tmp/sbproxy-ipc-501", directory);
        Assert.True(Encoding.UTF8.GetByteCount(longestEndpoint) < 104);
    }
}
