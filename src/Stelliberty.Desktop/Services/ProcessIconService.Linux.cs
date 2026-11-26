using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace Stelliberty.Desktop.Services;

internal static partial class ProcessIconService
{
    private static readonly Lazy<LinuxDesktopIcons> LinuxIcons = new(CreateLinuxDesktopIcons);

    private static Bitmap? ReadLinuxIcon(string path)
    {
        var desktop = LinuxIcons.Value;
        var executable = Path.GetFileName(path);
        if (!desktop.ByPath.TryGetValue(path, out var icon) &&
            !desktop.ByName.TryGetValue(executable, out icon))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(icon))
        {
            return File.Exists(icon) ? ReadLinuxIconFile(icon) : null;
        }

        foreach (var iconPath in FindLinuxThemeIcons(desktop, icon))
        {
            if (ReadLinuxIconFile(iconPath) is { } bitmap)
            {
                return bitmap;
            }
        }

        return null;
    }

    private static Bitmap? ReadLinuxIconFile(string iconPath)
    {
        if (string.Equals(Path.GetExtension(iconPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(iconPath);
            return LimitIconSize(new Bitmap(file));
        }

        try
        {
            return ReadLinuxPixbuf(iconPath);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static LinuxDesktopIcons CreateLinuxDesktopIcons()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            dataHome = Path.Combine(home, ".local", "share");
        }

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(dataDirs))
        {
            dataDirs = "/usr/local/share:/usr/share";
        }

        var dataRoots = dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(dataHome)
            .Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var desktop = new LinuxDesktopIcons(dataRoots, Path.Combine(home, ".icons"), ReadLinuxIconTheme(home));
        var desktopIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in dataRoots)
        {
            var applications = Path.Combine(root, "applications");
            if (!Directory.Exists(applications))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(applications, "*.desktop"))
            {
                if (desktopIds.Add(Path.GetFileName(file)))
                {
                    AddLinuxDesktopIcon(desktop, file);
                }
            }
        }

        return desktop;
    }

    private static void AddLinuxDesktopIcon(LinuxDesktopIcons desktop, string file)
    {
        string? executable = null;
        string? icon = null;
        var inDesktopEntry = false;
        var hidden = false;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith('['))
                {
                    if (inDesktopEntry)
                    {
                        break;
                    }

                    inDesktopEntry = line == "[Desktop Entry]";
                }

                if (!inDesktopEntry)
                {
                    continue;
                }

                if (line.StartsWith("Exec=", StringComparison.Ordinal))
                {
                    var command = line[5..].TrimStart();
                    var quoted = command.StartsWith('"');
                    var end = quoted ? command.IndexOf('"', 1) : command.IndexOfAny([' ', '\t']);
                    executable = command[(quoted ? 1 : 0)..(end >= 0 ? end : command.Length)];
                }
                else if (line.StartsWith("Icon=", StringComparison.Ordinal))
                {
                    icon = line[5..].Trim();
                }
                else if (line == "Hidden=true")
                {
                    hidden = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (hidden || string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(icon))
        {
            return;
        }

        if (Path.IsPathFullyQualified(executable))
        {
            desktop.ByPath.TryAdd(executable, icon);
        }
        else
        {
            desktop.ByName.TryAdd(executable, icon);
        }
    }

    private static string ReadLinuxIconTheme(string home)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(home, ".config");
        }

        foreach (var gtkVersion in new[] { "gtk-4.0", "gtk-3.0" })
        {
            var settings = Path.Combine(configHome, gtkVersion, "settings.ini");
            if (!File.Exists(settings))
            {
                continue;
            }

            try
            {
                foreach (var line in File.ReadLines(settings))
                {
                    var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && parts[0] == "gtk-icon-theme-name" &&
                        IsLinuxIconName(parts[1]))
                    {
                        return parts[1];
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 其余主题仍可用于查找。
            }
        }

        return "hicolor";
    }

    private static IEnumerable<string> FindLinuxThemeIcons(LinuxDesktopIcons desktop, string icon)
    {
        if (!IsLinuxIconName(icon))
        {
            yield break;
        }

        string[] extensions = [".png", ".svg", ".xpm"];
        string[] filenames = extensions.Contains(Path.GetExtension(icon), StringComparer.OrdinalIgnoreCase)
            ? [icon]
            : extensions.Select(extension => icon + extension).ToArray();
        var iconRoots = desktop.DataRoots.Select(root => Path.Combine(root, "icons")).Prepend(desktop.UserIcons);
        string[] sizes = ["128x128", "256x256", "scalable", "96x96", "64x64", "48x48", "32x32", "24x24", "16x16"];
        foreach (var theme in new[] { desktop.Theme, "hicolor", "Adwaita" }.Distinct(StringComparer.Ordinal))
        {
            foreach (var size in sizes)
            {
                foreach (var root in iconRoots)
                {
                    var path = FindLinuxIconFile(Path.Combine(root, theme, size, "apps"), filenames);
                    if (path is not null)
                    {
                        yield return path;
                    }
                }
            }
        }

        foreach (var root in desktop.DataRoots)
        {
            var path = FindLinuxIconFile(Path.Combine(root, "pixmaps"), filenames);
            if (path is not null)
            {
                yield return path;
            }
        }
    }

    private static bool IsLinuxIconName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && !name.Contains('/');

    private static string? FindLinuxIconFile(string directory, string[] filenames)
    {
        foreach (var filename in filenames)
        {
            var path = Path.Combine(directory, filename);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static Bitmap? ReadLinuxPixbuf(string file)
    {
        var pixbuf = LoadLinuxPixbuf(file, IconSize, IconSize, 1, 0);
        if (pixbuf == 0)
        {
            return null;
        }

        try
        {
            if (SaveLinuxPixbuf(pixbuf, out var buffer, out var size, "png", 0, 0, 0) == 0)
            {
                return null;
            }

            try
            {
                var bytes = new byte[checked((int)size)];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }
            finally
            {
                FreeLinuxBuffer(buffer);
            }
        }
        finally
        {
            ReleaseLinuxObject(pixbuf);
        }
    }

    private sealed record LinuxDesktopIcons(string[] DataRoots, string UserIcons, string Theme)
    {
        public Dictionary<string, string> ByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ByName { get; } = new(StringComparer.Ordinal);
    }

    [DllImport("libgdk_pixbuf-2.0.so.0", EntryPoint = "gdk_pixbuf_new_from_file_at_scale")]
    private static extern nint LoadLinuxPixbuf([MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        int width, int height, int preserveAspectRatio, nint error);

    [DllImport("libgdk_pixbuf-2.0.so.0", EntryPoint = "gdk_pixbuf_save_to_bufferv")]
    private static extern int SaveLinuxPixbuf(nint pixbuf, out nint buffer, out nuint size,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type, nint optionKeys, nint optionValues, nint error);

    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
    private static extern void ReleaseLinuxObject(nint value);

    [DllImport("libglib-2.0.so.0", EntryPoint = "g_free")]
    private static extern void FreeLinuxBuffer(nint value);
}
