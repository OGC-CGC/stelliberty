using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Stelliberty.Desktop.Services;

internal static partial class ProcessIconService
{
    private const int IconSize = 128;
    private const int CacheCapacity = 256;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, Task<Bitmap?>> Cache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly Queue<string> CompletedPaths = new();
    private static readonly SemaphoreSlim LoadSlots = new(4);

    public static Task<Bitmap?> GetAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (!Path.IsPathFullyQualified(path) && !(OperatingSystem.IsWindows() && IsWindowsDevicePath(path))))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        lock (CacheGate)
        {
            if (Cache.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var loading = Task.Run(() => LoadAsync(path));
            Cache.Add(path, loading);
            return loading;
        }
    }

    private static async Task<Bitmap?> LoadAsync(string path)
    {
        await LoadSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var bytes = await Dispatcher.UIThread.InvokeAsync(() => ReadMacIcon(path), DispatcherPriority.Background);
                if (bytes is not null)
                {
                    using var stream = new MemoryStream(bytes);
                    return LimitIconSize(new Bitmap(stream));
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                return ReadWindowsIcon(path);
            }
            else if (OperatingSystem.IsLinux())
            {
                return ReadLinuxIcon(path);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                          or InvalidOperationException or ExternalException or NotSupportedException
                                          or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            lock (CacheGate)
            {
                CompletedPaths.Enqueue(path);
                if (CompletedPaths.Count > CacheCapacity)
                {
                    Cache.Remove(CompletedPaths.Dequeue());
                }
            }

            LoadSlots.Release();
        }
    }

    private static Bitmap LimitIconSize(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var longestSide = Math.Max(size.Width, size.Height);
        if (longestSide <= IconSize)
        {
            return bitmap;
        }

        using (bitmap)
        {
            var scale = IconSize / (double)longestSide;
            return bitmap.CreateScaledBitmap(new PixelSize(
                Math.Max(1, (int)Math.Round(size.Width * scale)),
                Math.Max(1, (int)Math.Round(size.Height * scale))), BitmapInterpolationMode.HighQuality);
        }
    }
}
