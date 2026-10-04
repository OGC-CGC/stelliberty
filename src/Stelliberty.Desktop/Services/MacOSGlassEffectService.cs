using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Stelliberty.Desktop.Services;

internal sealed class MacOSGlassEffectService : IDisposable
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private static readonly nint ClearGlassStyle = 1;
    private static readonly nint TitleBarVisualEffectMaterial = 3;
    private static readonly nint WidthAndHeightSizable = 2 | 16;

    private nint _window;
    private nint _glassView;
    private nint _originalContentView;
    private nint _titleBarMaterialView;
    private bool _wasTitleBarMaterialHidden;

    public static bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(26)
                                      && GetClass("NSGlassEffectView") != nint.Zero;

    public bool Apply(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (!IsSupported)
        {
            return false;
        }

        var handle = window.TryGetPlatformHandle();
        if (handle is null
            || handle.Handle == nint.Zero
            || !string.Equals(handle.HandleDescriptor, "NSWindow", StringComparison.Ordinal))
        {
            return false;
        }

        if (_glassView != nint.Zero && _window == handle.Handle)
        {
            SetBool(_titleBarMaterialView, GetSelector("setHidden:"), true);
            return true;
        }

        Remove();

        var originalContentView = Send(handle.Handle, GetSelector("contentView"));
        if (originalContentView == nint.Zero)
        {
            return false;
        }

        var glassView = Send(Send(GetClass("NSGlassEffectView"), GetSelector("alloc")), GetSelector("init"));
        if (glassView == nint.Zero)
        {
            return false;
        }

        var titleBarMaterialView = FindTitleBarMaterialView(originalContentView);
        var wasTitleBarMaterialHidden = titleBarMaterialView != nint.Zero
                                        && SendBool(titleBarMaterialView, GetSelector("isHidden"));
        SetBool(titleBarMaterialView, GetSelector("setHidden:"), true);

        SetInteger(glassView, GetSelector("setStyle:"), ClearGlassStyle);
        SetPointer(glassView, GetSelector("setTintColor:"), nint.Zero);
        SetDouble(glassView, GetSelector("setCornerRadius:"), 20);
        SetInteger(glassView, GetSelector("setAutoresizingMask:"), WidthAndHeightSizable);

        Retain(originalContentView);
        SetPointer(glassView, GetSelector("setContentView:"), originalContentView);
        SetPointer(handle.Handle, GetSelector("setContentView:"), glassView);
        Release(originalContentView);
        Release(glassView);

        _window = handle.Handle;
        _glassView = glassView;
        _originalContentView = originalContentView;
        _titleBarMaterialView = titleBarMaterialView;
        _wasTitleBarMaterialHidden = wasTitleBarMaterialHidden;
        return true;
    }

    public void Remove()
    {
        if (_window == nint.Zero || _glassView == nint.Zero || _originalContentView == nint.Zero)
        {
            _window = nint.Zero;
            _glassView = nint.Zero;
            _originalContentView = nint.Zero;
            _titleBarMaterialView = nint.Zero;
            _wasTitleBarMaterialHidden = false;
            return;
        }

        Dispatcher.UIThread.VerifyAccess();

        var window = _window;
        var glassView = _glassView;
        var originalContentView = _originalContentView;
        var titleBarMaterialView = _titleBarMaterialView;
        var wasTitleBarMaterialHidden = _wasTitleBarMaterialHidden;
        _window = nint.Zero;
        _glassView = nint.Zero;
        _originalContentView = nint.Zero;
        _titleBarMaterialView = nint.Zero;
        _wasTitleBarMaterialHidden = false;

        Retain(originalContentView);
        SetPointer(glassView, GetSelector("setContentView:"), nint.Zero);
        SetPointer(window, GetSelector("setContentView:"), originalContentView);
        Release(originalContentView);
        SetBool(titleBarMaterialView, GetSelector("setHidden:"), wasTitleBarMaterialHidden);
    }

    public void Dispose()
    {
        Remove();
    }

    private static void Retain(nint value)
    {
        Send(value, GetSelector("retain"));
    }

    private static void Release(nint value)
    {
        SendVoid(value, GetSelector("release"));
    }

    private static nint FindTitleBarMaterialView(nint view)
    {
        var visualEffectViewClass = GetClass("NSVisualEffectView");
        if (SendBool(view, GetSelector("isKindOfClass:"), visualEffectViewClass)
            && Send(view, GetSelector("material")) == TitleBarVisualEffectMaterial)
        {
            return view;
        }

        var subviews = Send(view, GetSelector("subviews"));
        var count = Send(subviews, GetSelector("count"));
        for (nint index = 0; index < count; index++)
        {
            var match = FindTitleBarMaterialView(
                SendInteger(subviews, GetSelector("objectAtIndex:"), index));
            if (match != nint.Zero)
            {
                return match;
            }
        }

        return nint.Zero;
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(nint receiver, nint selector, nint value = default);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendInteger(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetPointer(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetInteger(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SetDouble(nint receiver, nint selector, double value);
}
