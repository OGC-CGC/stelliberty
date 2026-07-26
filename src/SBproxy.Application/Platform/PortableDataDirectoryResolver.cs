namespace SBproxy.Application.Platform;

public static class PortableDataDirectoryResolver
{
    private const string MacOSApplicationsDirectory = "/Applications";

    public static string ResolveMacOS(
        string baseDirectory,
        string userProfileDirectory,
        string bundleIdentifier)
    {
        var installDataDirectory = InstallDataDirectory(baseDirectory);
        var macOSDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        var contentsDirectory = macOSDirectory.Parent;
        var bundleDirectory = contentsDirectory?.Parent;
        if (macOSDirectory.Name != "MacOS"
            || contentsDirectory?.Name != "Contents"
            || bundleDirectory is not { Parent: { } bundleParent }
            || !string.Equals(bundleDirectory.Extension, ".app", StringComparison.OrdinalIgnoreCase))
        {
            return installDataDirectory;
        }

        return IsInstalledApplication(bundleDirectory.FullName)
            ? Path.Combine(userProfileDirectory, "Library", "Application Support", bundleIdentifier)
            : Path.Combine(bundleParent.FullName, $"{bundleIdentifier}.data");
    }

    public static string ResolveLinux(string baseDirectory, string? configuredDataDirectory)
    {
        return string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? InstallDataDirectory(baseDirectory)
            : Path.GetFullPath(configuredDataDirectory);
    }

    private static string InstallDataDirectory(string baseDirectory) =>
        Path.Combine(baseDirectory, PathConventions.DataDirectoryName);

    private static bool IsInstalledApplication(string bundlePath)
    {
        var normalizedBundlePath = Path.GetFullPath(bundlePath);
        var applicationsPrefix = MacOSApplicationsDirectory + Path.DirectorySeparatorChar;
        return normalizedBundlePath.StartsWith(applicationsPrefix, StringComparison.Ordinal);
    }
}
