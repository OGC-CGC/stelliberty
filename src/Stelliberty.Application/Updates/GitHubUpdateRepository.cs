using System.Diagnostics.CodeAnalysis;

namespace Stelliberty.Application.Updates;

public sealed record GitHubUpdateRepository(string RepositoryUrl, Uri ReleasesApiUri, string LatestReleasePageUrl)
{
    public const string DefaultRepositoryUrl = "https://github.com/OGC-CGC/stelliberty";

    public static bool TryParse(string value, [NotNullWhen(true)] out GitHubUpdateRepository? repository)
    {
        repository = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts.Length > 2 && !parts[2].Equals("releases", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var owner = parts[0];
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!IsRepositorySegment(owner) || !IsRepositorySegment(name))
        {
            return false;
        }

        var url = $"https://github.com/{owner}/{name}";
        repository = new(url, new Uri($"https://api.github.com/repos/{owner}/{name}/releases?per_page=30"), $"{url}/releases/latest");
        return true;
    }

    private static bool IsRepositorySegment(string value) => value.Length > 0 && value is not ("." or "..")
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
