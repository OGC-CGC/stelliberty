using System.Text.Json;
using Stelliberty.Application.Cli;
using Stelliberty.Application.Settings;
using Xunit;

namespace Stelliberty.Settings.Tests;

public sealed class CliCommandsTests
{
    [Fact]
    public void CliConsentDefaultsOffAndCannotBeChangedThroughCatalog()
    {
        Assert.False(new AppSettings().IsCliEnabled);
        Assert.DoesNotContain(CliCatalog.Settings, setting => setting.SettingProperty == nameof(AppSettings.IsCliEnabled));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-number")]
    public void MixedPortRejectsInvalidValues(string value) =>
        Assert.Throws<ArgumentException>(() => CliCatalog.Find("proxy config", "mixed-port").Parse(value));

    [Fact]
    public void OptionalPortCanBeDisabled() =>
        Assert.Equal(0, CliCatalog.Find("proxy config", "socks-port").Parse("0"));

    [Fact]
    public void DnsArraysCannotInjectExtraLines() =>
        Assert.Throws<ArgumentException>(() => CliCatalog.Find("dns", "nameservers").Parse("[\"1.1.1.1\\nother\"]"));

    [Fact]
    public void LiteralValueMayStartWithOptionPrefix()
    {
        var call = CliCommands.Parse(["proxy", "config", "set", "lan-password", "--dry-run", "--", "--secret"]);
        Assert.True(call.DryRun);
        Assert.Equal("--secret", call.Values[1]);
    }

    [Fact]
    public void ReadCommandsRejectDryRun() =>
        Assert.Throws<ArgumentException>(() => CliCommands.Parse(["settings", "get", "--dry-run"]));

    [Fact]
    public void DuplicateOptionsAreRejected() =>
        Assert.Throws<ArgumentException>(() => CliCommands.Parse(["proxy", "select", "--group", "one", "--group", "two"]));

    [Fact]
    public void EveryCommandAndSettingHasOfflineHelp()
    {
        foreach (var command in CliCommands.All)
            Assert.Contains(command.Description, CliCommands.Help(command.Path.Split(' ')));
        foreach (var setting in CliCatalog.Settings)
        {
            var help = CliCommands.Help([.. setting.Group.Split(' '), "set", setting.Name]);
            Assert.Contains(setting.Description, help);
            Assert.Contains("default_value", help);
        }
    }

    [Fact]
    public void CredentialSchemaDoesNotExposeDefaultValue()
    {
        var schema = JsonSerializer.SerializeToElement(CliCatalog.Find("proxy config", "lan-password").Schema);
        Assert.Equal(JsonValueKind.Null, schema.GetProperty("default_value").ValueKind);
        Assert.True(schema.GetProperty("sensitive").GetBoolean());
    }
}
