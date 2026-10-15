using System.Text;

namespace Stelliberty.Infrastructure.Platform;

internal static class MacOSCliPathProfile
{
    private const string EntryMarker = " # stelliberty-cli-managed-path-v1";

    internal static bool Apply(string profilePath, string entryText, bool isEnabled)
    {
        if (!isEnabled && !File.Exists(profilePath))
        {
            return false;
        }

        var original = File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : [];
        var entry = Encoding.UTF8.GetBytes(entryText);
        var updated = Rewrite(original, entry, isEnabled);
        if (original.AsSpan().SequenceEqual(updated))
        {
            return false;
        }

        if (isEnabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        }

        File.WriteAllBytes(profilePath, updated);
        return true;
    }

    internal static string BuildEntry(string cliBinaryPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(cliBinaryPath))!;
        var quotedDirectory = "'" + directory.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return $"export PATH={quotedDirectory}:\"$PATH\"{EntryMarker}";
    }

    private static byte[] Rewrite(byte[] original, byte[] entry, bool isEnabled)
    {
        var match = FindLastExactLine(original, entry);
        if (isEnabled)
        {
            if (match is not null)
            {
                return original;
            }

            var newline = FindNewline(original);
            var prefix = original.Length == 0 ? [] : newline;
            return [.. original, .. prefix, .. entry, .. newline];
        }

        if (match is null)
        {
            return original;
        }

        var start = match.Value.Start;
        var end = match.Value.TerminatorEnd;
        if (match.Value.IsLastLine && start > 0 && EndsWithNewline(original.AsSpan(0, start)))
        {
            start -= NewlineLengthBefore(original, start);
        }

        return [.. original.AsSpan(0, start), .. original.AsSpan(end)];
    }

    private static (int Start, int TerminatorEnd, bool IsLastLine)? FindLastExactLine(byte[] content, byte[] expected)
    {
        (int Start, int TerminatorEnd, bool IsLastLine)? match = null;
        var lineStart = 0;
        while (lineStart <= content.Length)
        {
            var newlineIndex = Array.IndexOf(content, (byte)'\n', lineStart);
            var lineEnd = newlineIndex < 0 ? content.Length : newlineIndex;
            if (lineEnd > lineStart && content[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            if (lineEnd - lineStart == expected.Length
                && content.AsSpan(lineStart, expected.Length).SequenceEqual(expected))
            {
                var terminatorEnd = newlineIndex < 0 ? content.Length : newlineIndex + 1;
                match = (lineStart, terminatorEnd, newlineIndex < 0 || terminatorEnd == content.Length);
            }

            if (newlineIndex < 0)
            {
                break;
            }

            lineStart = newlineIndex + 1;
        }

        return match;
    }

    private static byte[] FindNewline(byte[] content)
    {
        var newlineIndex = Array.IndexOf(content, (byte)'\n');
        return newlineIndex > 0 && content[newlineIndex - 1] == (byte)'\r'
            ? [(byte)'\r', (byte)'\n']
            : [(byte)'\n'];
    }

    private static bool EndsWithNewline(ReadOnlySpan<byte> content) => content[^1] == (byte)'\n';

    private static int NewlineLengthBefore(byte[] content, int index) =>
        index > 1 && content[index - 2] == (byte)'\r' ? 2 : 1;
}
