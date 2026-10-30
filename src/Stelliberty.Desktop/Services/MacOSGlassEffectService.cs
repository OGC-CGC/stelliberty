using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Stelliberty.Desktop.Services;

internal sealed class MacOSGlassEffectService : IDisposable
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    // NSGlassEffectViewStyleClear 比 Regular 更通透，Frutiger Aero 以最透明为目标。
    private static readonly nint ClearGlassStyle = 1;
    private static readonly nint NoTitleBarSeparatorStyle = 1;
    private static readonly nint TitleBarVisualEffectMaterial = 3;
    private static readonly nint WidthAndHeightSizable = 2 | 16;

    private nint _window;
    private nint _glassView;
    private nint _originalContentView;
    private nint _titleBarMaterialView;
    private nint _titleBarUnderlineView;
    private nint _originalTitleBarSeparatorStyle;
    private bool _wasTitleBarMaterialHidden;
    private bool _wasTitleBarUnderlineHidden;

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
            SetInteger(_glassView, GetSelector("setStyle:"), ClearGlassStyle);
            // 全屏切换会重建标题栏视图：按当前视图树重新定位后再隐藏，避免向已释放的视图发消息。
            _titleBarMaterialView = FindTitleBarMaterialView(_originalContentView);
            _titleBarUnderlineView = FindTitleBarUnderline(_originalContentView);
            SetBool(_titleBarMaterialView, GetSelector("setHidden:"), true);
            SetBool(_titleBarUnderlineView, GetSelector("setHidden:"), true);
            HideTitleBarSeparator(_window);
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
        // 原生材质隐藏后会露出 Avalonia 自带的标题栏下划线，需一并隐藏。
        var titleBarUnderlineView = FindTitleBarUnderline(originalContentView);
        var wasTitleBarUnderlineHidden = titleBarUnderlineView != nint.Zero
                                         && SendBool(titleBarUnderlineView, GetSelector("isHidden"));
        var originalTitleBarSeparatorStyle = Send(handle.Handle, GetSelector("titlebarSeparatorStyle"));
        SetBool(titleBarMaterialView, GetSelector("setHidden:"), true);
        SetBool(titleBarUnderlineView, GetSelector("setHidden:"), true);

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
        _titleBarUnderlineView = titleBarUnderlineView;
        _originalTitleBarSeparatorStyle = originalTitleBarSeparatorStyle;
        _wasTitleBarMaterialHidden = wasTitleBarMaterialHidden;
        _wasTitleBarUnderlineHidden = wasTitleBarUnderlineHidden;
        HideTitleBarSeparator(handle.Handle);
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
            _titleBarUnderlineView = nint.Zero;
            _originalTitleBarSeparatorStyle = nint.Zero;
            _wasTitleBarMaterialHidden = false;
            _wasTitleBarUnderlineHidden = false;
            return;
        }

        Dispatcher.UIThread.VerifyAccess();

        var window = _window;
        var glassView = _glassView;
        var originalContentView = _originalContentView;
        var titleBarMaterialView = _titleBarMaterialView;
        var titleBarUnderlineView = _titleBarUnderlineView;
        var originalTitleBarSeparatorStyle = _originalTitleBarSeparatorStyle;
        var wasTitleBarMaterialHidden = _wasTitleBarMaterialHidden;
        var wasTitleBarUnderlineHidden = _wasTitleBarUnderlineHidden;
        _window = nint.Zero;
        _glassView = nint.Zero;
        _originalContentView = nint.Zero;
        _titleBarMaterialView = nint.Zero;
        _titleBarUnderlineView = nint.Zero;
        _originalTitleBarSeparatorStyle = nint.Zero;
        _wasTitleBarMaterialHidden = false;
        _wasTitleBarUnderlineHidden = false;

        Retain(originalContentView);
        SetPointer(glassView, GetSelector("setContentView:"), nint.Zero);
        SetPointer(window, GetSelector("setContentView:"), originalContentView);
        Release(originalContentView);
        SetBool(titleBarMaterialView, GetSelector("setHidden:"), wasTitleBarMaterialHidden);
        SetBool(titleBarUnderlineView, GetSelector("setHidden:"), wasTitleBarUnderlineHidden);
        SetInteger(window, GetSelector("setTitlebarSeparatorStyle:"), originalTitleBarSeparatorStyle);
    }

    public void Dispose()
    {
        Remove();
    }

    private static void Retain(nint value)
    {
        Send(value, GetSelector("retain"));
    }

    private void HideTitleBarSeparator(nint window)
    {
        SetInteger(window, GetSelector("setTitlebarSeparatorStyle:"), NoTitleBarSeparatorStyle);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_window == window && _glassView != nint.Zero)
                {
                    SetInteger(window, GetSelector("setTitlebarSeparatorStyle:"), NoTitleBarSeparatorStyle);
                }
            },
            DispatcherPriority.ApplicationIdle);
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

    // Avalonia.Native 在标题栏底部放置 NSBox 作为下划线；玻璃材质隐藏后必须一并隐藏。
    private static nint FindTitleBarUnderline(nint view)
    {
        var boxClass = GetClass("NSBox");
        if (SendBool(view, GetSelector("isKindOfClass:"), boxClass))
        {
            return view;
        }

        var subviews = Send(view, GetSelector("subviews"));
        var count = Send(subviews, GetSelector("count"));
        for (nint index = 0; index < count; index++)
        {
            var match = FindTitleBarUnderline(
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
