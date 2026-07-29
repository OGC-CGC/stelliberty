#if DEBUG
using System.Text.Json;
using SBproxy.Application.Runtime;

namespace SBproxy.Desktop.Debug;

internal static class CoreStatusFormatter
{
    public static async Task<string> FormatAsync(IAgentClient coreManager)
    {
        var snapshot = await coreManager.GetSnapshotAsync();
        return JsonSerializer.Serialize(new
        {
            state = snapshot.State.ToString(),
            pid = snapshot.Pid,
            controller = snapshot.ExternalController,
            error = snapshot.LastError
        });
    }
}
#endif
