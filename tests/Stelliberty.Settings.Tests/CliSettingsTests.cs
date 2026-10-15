using Stelliberty.Application.Cli;
using Stelliberty.Application.Settings;
using Xunit;

namespace Stelliberty.Settings.Tests;

public sealed class CliSettingsTests
{
    [Fact]
    public void CatalogExamplesCanBeAssignedWithoutAViewModel()
    {
        foreach (var field in CliCatalog.Settings)
        {
            var text = field.Type switch
            {
                "boolean" => "true",
                "integer" => (field.Minimum ?? 1).ToString(),
                "lines" => "[\"one\",\"two\"]",
                _ => field.Options?.Split('|')[0] ?? (field.Name switch
                {
                    "accent-color" => "#AABBCC",
                    "delay-test-url" => "https://example.com/",
                    _ => "example"
                })
            };
            CliSettings.Assign(new AppSettings(), field, field.Parse(text));
        }
    }

    [Fact]
    public void DisabledOptionalPortIsPersistedAsNullAndReadAsZero()
    {
        var settings = new AppSettings { SocksPort = 7890 };
        var field = CliCatalog.Find("proxy config", "socks-port");
        CliSettings.Assign(settings, field, field.Parse("0"));
        Assert.Null(settings.SocksPort);
        Assert.Equal(0, CliSettings.Read(settings, field));
    }

    [Fact]
    public void UnspecifiedTunMtuUsesTheExistingDefault()
    {
        var settings = new AppSettings { TunMtu = null };
        Assert.Equal(AppSettings.DefaultTunMtu, CliSettings.Read(settings, CliCatalog.Find("proxy config", "tun-mtu")));
    }

    [Fact]
    public void CustomAccentAlsoChangesAccentMode()
    {
        var settings = new AppSettings();
        var field = CliCatalog.Find("settings", "accent-color");
        CliSettings.Assign(settings, field, field.Parse("#112233"));
        Assert.Equal("Custom", settings.AccentColorMode);
        Assert.Equal("#112233", settings.AccentColor);
        Assert.Throws<ArgumentException>(() => CliSettings.Assign(settings, field, "invalid"));
        Assert.Equal("#112233", settings.AccentColor);
    }

    [Fact]
    public void DnsListsAreNormalizedAndRedacted()
    {
        var settings = new AppSettings();
        var field = CliCatalog.Find("dns", "nameservers");
        CliSettings.Assign(settings, field, field.Parse("[\" 1.1.1.1 \",\"\",\"8.8.8.8\"]"));
        Assert.Equal(new[] { "1.1.1.1", "8.8.8.8" }, settings.NameServers);
        Assert.Equal("<redacted>", CliSettings.Read(settings, field));
    }
}
