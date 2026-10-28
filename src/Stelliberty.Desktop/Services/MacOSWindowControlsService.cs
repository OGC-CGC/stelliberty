using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Stelliberty.Desktop.Services;

// 自绘窗口按钮时隐藏 macOS 系统红绿灯，避免与自绘控制重复。
internal sealed class MacOSWindowControlsService
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const int CloseButton = 0;
    private const int MiniaturizeButton = 1;
    private const int ZoomButton = 2;

    public void SetHidden(Window window, bool isHidden)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var handle = window.TryGetPlatformHandle();
        if (handle is null
            || handle.Handle == nint.Zero
            || !string.Equals(handle.HandleDescriptor, "NSWindow", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var buttonType in new[] { CloseButton, MiniaturizeButton, ZoomButton })
        {
            var button = SendInteger(handle.Handle, GetSelector("standardWindowButton:"), buttonType);
            if (button != nint.Zero)
            {
                SetBool(button, GetSelector("setHidden:"), isHidden);
            }
        }
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendInteger(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);
}
