namespace SBproxy.Application.Platform;

public interface ISystemProxyService
{
    SystemProxyOperationResult Enable(SystemProxyApplicationRequest request);

    SystemProxyOperationResult Disable();
}
