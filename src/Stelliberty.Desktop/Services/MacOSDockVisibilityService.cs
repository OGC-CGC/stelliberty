using System.Runtime.InteropServices;
using Stelliberty.Application.Diagnostics;

namespace Stelliberty.Desktop.Services;

internal static class MacOSDockVisibilityService
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private static readonly nint RegularActivationPolicy = 0;
    private static readonly nint AccessoryActivationPolicy = 1;

    public static void SetVisible(bool isVisible)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var applicationClass = GetObjectiveCClass("NSApplication");
            var application = applicationClass == IntPtr.Zero
                ? IntPtr.Zero
                : SendIntPtr(applicationClass, RegisterSelector("sharedApplication"));
            if (application == IntPtr.Zero)
            {
                AppLogger.Warning("macOS Dock visibility update failed: NSApplication is unavailable");
                return;
            }

            var activationPolicy = isVisible ? RegularActivationPolicy : AccessoryActivationPolicy;
            if (SendByteNInt(application, RegisterSelector("setActivationPolicy:"), activationPolicy) == 0)
            {
                AppLogger.Warning($"macOS Dock visibility update was rejected: visible={isVisible}");
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"macOS Dock visibility update failed: {exception.Message}");
        }
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetObjectiveCClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string className);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string selectorName);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern byte SendByteNInt(IntPtr receiver, IntPtr selector, nint value);
}
