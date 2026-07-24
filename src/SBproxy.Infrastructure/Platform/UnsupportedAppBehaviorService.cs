using SBproxy.Application.Diagnostics;
using SBproxy.Application.Platform;

namespace SBproxy.Infrastructure.Platform;

public sealed class UnsupportedAppBehaviorService : IAppBehaviorService
{
    public void Apply(AppBehaviorApplicationRequest request)
    {
        AppLogger.Info($"App behavior request recorded: autoStart={request.IsAutoStartEnabled}");
    }
}
