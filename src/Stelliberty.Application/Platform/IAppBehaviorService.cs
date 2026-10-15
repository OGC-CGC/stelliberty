namespace Stelliberty.Application.Platform;

public interface IAppBehaviorService
{
    void Apply(AppBehaviorApplicationRequest request);

    string? BuildCliPathEntry() => null;

    bool ApplyCliPathEntry(string? entry, bool isEnabled) => false;
}
