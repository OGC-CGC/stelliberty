namespace SBproxy.Application.Platform;

public interface INetworkConnectionProbe
{
    NetworkConnectionInfo Detect();
}
