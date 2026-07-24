using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Stelliberty.Application.Diagnostics;
using Stelliberty.Application.Localization;
using Stelliberty.Application.Platform;

namespace Stelliberty.Desktop.Services;

internal sealed class MacOSApplicationMenuService : IDisposable
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const nuint ServicesItemIndex = 4;

    private readonly NativeMenu _menu = new();
    private readonly NativeMenu _servicesMenu = new();
    private readonly NativeMenuItem _aboutItem = new();
    private readonly NativeMenuItem _settingsItem = new();
    private readonly NativeMenuItem _servicesItem = new();
    private readonly NativeMenuItem _hideItem = new();
    private readonly NativeMenuItem _hideOthersItem = new();
    private readonly NativeMenuItem _showAllItem = new();
    private readonly NativeMenuItem _quitItem = new();

    private ILocalizationService? _localization;
    private Action? _showAbout;
    private Action? _showSettings;
    private Action? _requestExit;
    private bool _isInitialized;

    public MacOSApplicationMenuService()
    {
        _servicesItem.Menu = _servicesMenu;
        _settingsItem.Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta);
        _hideItem.Gesture = new KeyGesture(Key.H, KeyModifiers.Meta);
        _hideOthersItem.Gesture = new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt);
        _quitItem.Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta);

        _menu.Add(_aboutItem);
        _menu.Add(new NativeMenuItemSeparator());
        _menu.Add(_settingsItem);
        _menu.Add(new NativeMenuItemSeparator());
        _menu.Add(_servicesItem);
        _menu.Add(new NativeMenuItemSeparator());
        _menu.Add(_hideItem);
        _menu.Add(_hideOthersItem);
        _menu.Add(_showAllItem);
        _menu.Add(new NativeMenuItemSeparator());
        _menu.Add(_quitItem);
    }

    public void Initialize(Avalonia.Application application)
    {
        if (!OperatingSystem.IsMacOS() || _isInitialized)
        {
            return;
        }

        _isInitialized = true;
        _menu.NeedsUpdate += OnMenuNeedsUpdate;
        _aboutItem.Click += OnAboutClicked;
        _settingsItem.Click += OnSettingsClicked;
        _hideItem.Click += OnHideClicked;
        _hideOthersItem.Click += OnHideOthersClicked;
        _showAllItem.Click += OnShowAllClicked;
        _quitItem.Click += OnQuitClicked;
        NativeMenu.SetMenu(application, _menu);
    }

    public void Attach(
        ILocalizationService localization,
        Action showAbout,
        Action showSettings,
        Action requestExit)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
        }

        _localization = localization;
        _showAbout = showAbout;
        _showSettings = showSettings;
        _requestExit = requestExit;
        _localization.LanguageChanged += OnLanguageChanged;
        UpdateText();
    }

    public void Dispose()
    {
        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
            _localization = null;
        }

        _showAbout = null;
        _showSettings = null;
        _requestExit = null;
        if (!_isInitialized)
        {
            return;
        }

        _isInitialized = false;
        _menu.NeedsUpdate -= OnMenuNeedsUpdate;
        _aboutItem.Click -= OnAboutClicked;
        _settingsItem.Click -= OnSettingsClicked;
        _hideItem.Click -= OnHideClicked;
        _hideOthersItem.Click -= OnHideOthersClicked;
        _showAllItem.Click -= OnShowAllClicked;
        _quitItem.Click -= OnQuitClicked;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateText();
            return;
        }

        Dispatcher.UIThread.Post(UpdateText);
    }

    private void UpdateText()
    {
        if (_localization is null)
        {
            return;
        }

        _aboutItem.Header = Format("MacOS.ApplicationMenu.About");
        _settingsItem.Header = _localization.GetString("MacOS.ApplicationMenu.Settings");
        _servicesItem.Header = _localization.GetString("MacOS.ApplicationMenu.Services");
        _hideItem.Header = Format("MacOS.ApplicationMenu.Hide");
        _hideOthersItem.Header = _localization.GetString("MacOS.ApplicationMenu.HideOthers");
        _showAllItem.Header = _localization.GetString("MacOS.ApplicationMenu.ShowAll");
        _quitItem.Header = Format("MacOS.ApplicationMenu.Quit");
    }

    private string Format(string key)
    {
        return string.Format(_localization!.GetString(key), AppMetadata.DisplayName);
    }

    private void OnMenuNeedsUpdate(object? sender, EventArgs args)
    {
        TryAttachSystemServicesMenu();
    }

    private void OnAboutClicked(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() => _showAbout?.Invoke());
    }

    private void OnSettingsClicked(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() => _showSettings?.Invoke());
    }

    private static void OnHideClicked(object? sender, EventArgs args)
    {
        SendApplicationAction("hide:");
    }

    private static void OnHideOthersClicked(object? sender, EventArgs args)
    {
        SendApplicationAction("hideOtherApplications:");
    }

    private static void OnShowAllClicked(object? sender, EventArgs args)
    {
        SendApplicationAction("unhideAllApplications:");
    }

    private void OnQuitClicked(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() => _requestExit?.Invoke());
    }

    private static void SendApplicationAction(string selectorName)
    {
        try
        {
            var application = GetSharedApplication();
            if (application != IntPtr.Zero)
            {
                SendVoidIntPtr(application, RegisterSelector(selectorName), IntPtr.Zero);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"macOS application menu action failed ({selectorName}): {exception.Message}");
        }
    }

    private static void TryAttachSystemServicesMenu()
    {
        try
        {
            var application = GetSharedApplication();
            var mainMenu = SendIntPtr(application, RegisterSelector("mainMenu"));
            if (mainMenu == IntPtr.Zero || SendNUInt(mainMenu, RegisterSelector("numberOfItems")) == 0)
            {
                return;
            }

            var itemAtIndex = RegisterSelector("itemAtIndex:");
            var applicationMenuItem = SendIntPtrNUInt(mainMenu, itemAtIndex, 0);
            var applicationMenu = SendIntPtr(applicationMenuItem, RegisterSelector("submenu"));
            if (applicationMenu == IntPtr.Zero
                || SendNUInt(applicationMenu, RegisterSelector("numberOfItems")) <= ServicesItemIndex)
            {
                return;
            }

            var servicesItem = SendIntPtrNUInt(applicationMenu, itemAtIndex, ServicesItemIndex);
            var servicesMenu = SendIntPtr(servicesItem, RegisterSelector("submenu"));
            if (servicesMenu != IntPtr.Zero)
            {
                SendVoidIntPtr(application, RegisterSelector("setServicesMenu:"), servicesMenu);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"macOS Services menu attachment failed: {exception.Message}");
        }
    }

    private static IntPtr GetSharedApplication()
    {
        var applicationClass = GetObjectiveCClass("NSApplication");
        return applicationClass == IntPtr.Zero
            ? IntPtr.Zero
            : SendIntPtr(applicationClass, RegisterSelector("sharedApplication"));
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
    private static extern IntPtr SendIntPtrNUInt(IntPtr receiver, IntPtr selector, nuint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidIntPtr(IntPtr receiver, IntPtr selector, IntPtr value);
}
