using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using Stelliberty.Application.Diagnostics;

namespace Stelliberty.Desktop;

[SupportedOSPlatform("macos")]
internal static class MacDockIconService
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    public static void SetPackagedIcon()
    {
        // AppKit 由 Avalonia 初始化，图标设置必须在主线程执行。
        Dispatcher.UIThread.VerifyAccess();
        // 发布包 UI 位于 Contents/MacOS/data/deps。
        var appBundlePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        var iconPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Resources", "AppIcon.icns"));

        try
        {
            SetIcon(appBundlePath, iconPath);
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"macOS Dock icon could not be set: {exception.Message}");
        }
    }

    private static void SetIcon(string appBundlePath, string fallbackIconPath)
    {
        nint nsImage = nint.Zero;
        if (OperatingSystem.IsMacOSVersionAtLeast(26))
        {
            nsImage = LoadSystemApplicationIcon(appBundlePath);
        }

        if (nsImage == nint.Zero)
        {
            nsImage = LoadImage(fallbackIconPath);
        }

        if (nsImage == nint.Zero)
        {
            AppLogger.Warning($"macOS Dock icon could not be loaded: {fallbackIconPath}");
            return;
        }

        try
        {
            var application = Send(GetClass("NSApplication"), GetSelector("sharedApplication"));
            SetApplicationIcon(application, GetSelector("setApplicationIconImage:"), nsImage);
        }
        finally
        {
            // 应用持有设置后的图像，此处仅释放本地所有权。
            Release(nsImage, GetSelector("release"));
        }
    }

    private static nint LoadSystemApplicationIcon(string appBundlePath)
    {
        var nsPath = CreateString(appBundlePath);
        try
        {
            var workspace = Send(GetClass("NSWorkspace"), GetSelector("sharedWorkspace"));
            var nsImage = Send(workspace, GetSelector("iconForFile:"), nsPath);
            return nsImage == nint.Zero ? nint.Zero : Send(nsImage, GetSelector("retain"));
        }
        finally
        {
            Release(nsPath, GetSelector("release"));
        }
    }

    private static nint LoadImage(string iconPath)
    {
        if (!File.Exists(iconPath))
        {
            return nint.Zero;
        }

        var nsPath = CreateString(iconPath);
        try
        {
            return Send(Send(GetClass("NSImage"), GetSelector("alloc")),
                GetSelector("initWithContentsOfFile:"), nsPath);
        }
        finally
        {
            Release(nsPath, GetSelector("release"));
        }
    }

    private static nint CreateString(string value)
    {
        var utf8Value = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Send(GetClass("NSString"), GetSelector("alloc")),
                GetSelector("initWithUTF8String:"), utf8Value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8Value);
        }
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetApplicationIcon(nint receiver, nint selector, nint image);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void Release(nint receiver, nint selector);
}
