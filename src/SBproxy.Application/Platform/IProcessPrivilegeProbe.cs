namespace SBproxy.Application.Platform;

public interface IProcessPrivilegeProbe
{
    ProcessRunMode Detect();
}
