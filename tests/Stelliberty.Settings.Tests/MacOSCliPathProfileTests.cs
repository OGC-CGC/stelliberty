using System.Text;
using Stelliberty.Infrastructure.Platform;
using Xunit;

namespace Stelliberty.Settings.Tests;

public sealed class MacOSCliPathProfileTests
{
    [Fact]
    public void DisableRemovesOnlyTheExactManagedLineAndPreservesOtherBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stelliberty-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var profilePath = Path.Combine(directory, ".profile");
        var cliDirectory = Path.Combine(directory, "Stelliberty's App", "Contents", "MacOS", "tweaks", "cli");
        var cliPath = Path.Combine(cliDirectory, "stelliberty-cli");
        var managedEntry = MacOSCliPathProfile.BuildEntry(cliPath);
        var original = Encoding.UTF8.GetBytes(
            $"# preserve CRLF and missing final newline\r\n{managedEntry.Replace("export PATH=", "export  PATH=", StringComparison.Ordinal)}\r\nlast line");
        File.WriteAllBytes(profilePath, original);

        try
        {
            Assert.True(MacOSCliPathProfile.Apply(profilePath, managedEntry, isEnabled: true));
            Assert.False(MacOSCliPathProfile.Apply(profilePath, managedEntry, isEnabled: true));
            Assert.True(MacOSCliPathProfile.Apply(profilePath, managedEntry, isEnabled: false));

            Assert.Equal(original, File.ReadAllBytes(profilePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
