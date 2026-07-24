using SBproxy.Application.Settings;

namespace SBproxy.Application.Platform;

public interface IWindowEffectCapability
{
    IReadOnlyList<WindowEffect> SupportedEffects { get; }
}
