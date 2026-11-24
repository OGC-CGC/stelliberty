namespace Stelliberty.Presentation.Proxies;

internal static class ProxyDelayPresentation
{
    public static string GetLevel(int? delay) => delay switch
    {
        null => "delay-none",
        < 0 => "delay-bad",
        < 120 => "delay-good",
        < 240 => "delay-mid",
        _ => "delay-slow"
    };
}
