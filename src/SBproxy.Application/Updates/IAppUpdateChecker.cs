namespace SBproxy.Application.Updates;

public interface IAppUpdateChecker
{
    AppUpdateCheckResult CheckForUpdates();
}
