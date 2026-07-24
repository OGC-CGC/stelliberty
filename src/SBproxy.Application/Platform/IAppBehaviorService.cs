namespace SBproxy.Application.Platform;

public interface IAppBehaviorService
{
    void Apply(AppBehaviorApplicationRequest request);
}
