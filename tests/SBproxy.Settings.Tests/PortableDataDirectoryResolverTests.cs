using SBproxy.Application.Platform;
using Xunit;

namespace SBproxy.Settings.Tests;

public sealed class PortableDataDirectoryResolverTests
{
    private const string BundleIdentifier = "com.example.app";
    private const string UserProfileDirectory = "/Users/test";

    [Fact(DisplayName = "macOS app under Applications uses user Application Support")]
    public void MacOSInstalledAppUsesUserApplicationSupport()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var result = PortableDataDirectoryResolver.ResolveMacOS(
            "/Applications/Example.app/Contents/MacOS/",
            UserProfileDirectory,
            BundleIdentifier);

        Assert.Equal(
            "/Users/test/Library/Application Support/com.example.app",
            result);
    }

    [Theory(DisplayName = "macOS app outside Applications uses data beside bundle")]
    [InlineData(
        "/Volumes/External/Example.app/Contents/MacOS/",
        "/Volumes/External/com.example.app.data")]
    [InlineData(
        "/ApplicationsBackup/Example.app/Contents/MacOS/",
        "/ApplicationsBackup/com.example.app.data")]
    public void MacOSPortableAppUsesDataBesideBundle(string baseDirectory, string expected)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var result = PortableDataDirectoryResolver.ResolveMacOS(
            baseDirectory,
            UserProfileDirectory,
            BundleIdentifier);

        Assert.Equal(expected, result);
    }

    [Fact(DisplayName = "macOS non-app process uses data under base directory")]
    public void MacOSNonAppProcessUsesDataUnderBaseDirectory()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var result = PortableDataDirectoryResolver.ResolveMacOS(
            "/workspace/bin/",
            UserProfileDirectory,
            BundleIdentifier);

        Assert.Equal("/workspace/bin/data", result);
    }
}
