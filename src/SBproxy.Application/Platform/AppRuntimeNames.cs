using System.Text;

namespace SBproxy.Application.Platform;

public static class AppRuntimeNames
{
    private const string ServiceAppName = "sbproxyrev";

#if DEBUG
    public const string ChannelName = "Debug";
    private const string DevSuffix = "Dev";
    private const string FileDevSuffix = "_dev";
#else
    public const string ChannelName = "Release";
    private const string DevSuffix = "";
    private const string FileDevSuffix = "";
#endif

    public static string ServiceName => $"{PascalIdentifier(ServiceAppName)}Service{DevSuffix}";

    public static string ServiceBinaryName => OperatingSystem.IsWindows()
        ? $"{FileToken(ServiceAppName)}_service.exe"
        : $"{FileToken(ServiceAppName)}_service";

    public static string ServiceInstalledBinaryStem => $"{FileToken(ServiceAppName)}_service_host{FileDevSuffix}";

    public static string CoreLockPrefix => $".{FileToken(AppMetadata.Name)}-core-";

    public static string UserAgent => $"{FileToken(AppMetadata.Name)}/{AppMetadata.Version}";

    public static string ResourceAuthority => AppMetadata.Name;

    public static string FileNameToken => FileToken(AppMetadata.Name);

    private static string PascalIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        var capitalizeNext = true;
        foreach (var ch in value)
        {
            if (!IsAsciiLetterOrDigit(ch))
            {
                capitalizeNext = true;
                continue;
            }

            builder.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
            capitalizeNext = false;
        }

        return builder.Length == 0 ? "App" : builder.ToString();
    }

    private static string FileToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var ch in value)
        {
            if (IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('_') is { Length: > 0 } token ? token : "app";
    }

    private static bool IsAsciiLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';
    }
}
