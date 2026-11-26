using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Stelliberty.Desktop.Services;

internal static partial class ProcessIconService
{
    private static bool IsWindowsDevicePath(string path) =>
        path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase);

    private static Bitmap? ReadWindowsIcon(string path)
    {
        if (IsWindowsDevicePath(path) && ResolveWindowsDevicePath(path) is { } resolved)
        {
            path = resolved;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        var initialized = CoInitializeEx(0, 0) >= 0;
        try
        {
            if (SHDefExtractIcon(path, 0, 0, out var icon, 0, IconSize) == 0 &&
                ReadOwnedWindowsIcon(icon, IconSize) is { } extracted)
            {
                return extracted;
            }

            const uint getIcon = 0x100;
            var result = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), getIcon);
            if (result == 0 || info.Icon == 0)
            {
                return null;
            }

            const int systemLargeIconWidth = 11;
            var size = GetSystemMetrics(systemLargeIconWidth);
            return ReadOwnedWindowsIcon(info.Icon, size > 0 ? size : 32);
        }
        finally
        {
            if (initialized)
            {
                CoUninitialize();
            }
        }
    }

    private static Bitmap? ReadOwnedWindowsIcon(nint icon, int size)
    {
        if (icon == 0)
        {
            return null;
        }

        try
        {
            return DrawWindowsIcon(icon, size);
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private static string? ResolveWindowsDevicePath(string path)
    {
        var target = new StringBuilder(1024);
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var driveName = drive[..2];
            if (QueryDosDevice(driveName, target, target.Capacity) == 0)
            {
                continue;
            }

            // QueryDosDevice 返回当前映射及历史映射，首项是当前映射。
            var prefix = target.ToString().Split('\0', 2)[0];
            if (path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return driveName + path[prefix.Length..];
            }
        }

        return null;
    }

    private static Bitmap? DrawWindowsIcon(nint icon, int size)
    {
        const uint drawNormal = 3;
        const uint drawMask = 1;
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };
        var dc = CreateCompatibleDC(0);
        if (dc == 0)
        {
            return null;
        }

        try
        {
            var dib = CreateDIBSection(dc, ref header, 0, out var data, 0, 0);
            if (dib == 0)
            {
                return null;
            }

            try
            {
                var previous = SelectObject(dc, dib);
                if (previous == 0)
                {
                    return null;
                }

                try
                {
                    var pixels = new int[size * size];
                    Marshal.Copy(pixels, 0, data, pixels.Length);
                    if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, 0, drawNormal))
                    {
                        return null;
                    }

                    GdiFlush();
                    Marshal.Copy(data, pixels, 0, pixels.Length);
                    // 传统图标没有 alpha 通道，透明区域由 AND 掩码标记。
                    if (!pixels.Any(pixel => (pixel >>> 24) != 0))
                    {
                        if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, 0, drawMask))
                        {
                            return null;
                        }

                        GdiFlush();
                        var mask = new int[pixels.Length];
                        Marshal.Copy(data, mask, 0, mask.Length);
                        for (var i = 0; i < pixels.Length; i++)
                        {
                            pixels[i] = (mask[i] & 0xffffff) == 0 ? pixels[i] | unchecked((int)0xff000000) : 0;
                        }
                    }

                    var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
                        PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using (var frame = bitmap.Lock())
                    {
                        for (var row = 0; row < size; row++)
                        {
                            Marshal.Copy(pixels, row * size, frame.Address + row * frame.RowBytes, size);
                        }
                    }

                    return LimitIconSize(bitmap);
                }
                finally
                {
                    SelectObject(dc, previous);
                }
            }
            finally
            {
                DeleteObject(dib);
            }
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint mode);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maxLength);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIcon(string path, int index, uint flags, out nint largeIcon, nint smallIcon, uint size);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint data, nint section, uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
