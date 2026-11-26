using System.Runtime.InteropServices;

namespace Stelliberty.Desktop.Services;

internal static partial class ProcessIconService
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    private static byte[]? ReadMacIcon(string path)
    {
        // 应用的辅助进程也使用最外层应用包的图标。
        var bundleEnd = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        var iconPath = bundleEnd >= 0 ? path[..(bundleEnd + 4)] : path;
        if (!File.Exists(iconPath) && !Directory.Exists(iconPath))
        {
            return null;
        }

        var pool = SendMacMessage(GetMacClass("NSAutoreleasePool"), GetMacSelector("new"));
        try
        {
            var pathString = CreateMacString(GetMacClass("NSString"), GetMacSelector("stringWithUTF8String:"), iconPath);
            var workspace = SendMacMessage(GetMacClass("NSWorkspace"), GetMacSelector("sharedWorkspace"));
            var image = SendMacMessage(workspace, GetMacSelector("iconForFile:"), pathString);
            var rect = new MacRect { X = 0, Y = 0, Width = IconSize, Height = IconSize };
            var cgImage = GetMacCGImage(image, GetMacSelector("CGImageForProposedRect:context:hints:"), ref rect, 0, 0);
            if (cgImage == 0)
            {
                return null;
            }

            var bitmapClass = GetMacClass("NSBitmapImageRep");
            var bitmap = SendMacMessage(SendMacMessage(bitmapClass, GetMacSelector("alloc")),
                GetMacSelector("initWithCGImage:"), cgImage);
            try
            {
                const int pngFileType = 4;
                var data = GetMacImageData(bitmap, GetMacSelector("representationUsingType:properties:"), pngFileType, 0);
                var length = checked((int)SendMacMessage(data, GetMacSelector("length")));
                if (length == 0)
                {
                    return null;
                }

                var bytes = new byte[length];
                Marshal.Copy(SendMacMessage(data, GetMacSelector("bytes")), bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                SendMacVoidMessage(bitmap, GetMacSelector("release"));
            }
        }
        finally
        {
            SendMacVoidMessage(pool, GetMacSelector("drain"));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint GetMacClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint GetMacSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendMacMessage(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendMacMessage(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendMacVoidMessage(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint CreateMacString(nint receiver, nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint GetMacCGImage(nint receiver, nint selector, ref MacRect rect, nint context, nint hints);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint GetMacImageData(nint receiver, nint selector, int fileType, nint properties);
}
