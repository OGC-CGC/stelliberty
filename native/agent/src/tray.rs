use anyhow::{Context, Result};
use global_hotkey::{GlobalHotKeyEvent, GlobalHotKeyManager, HotKeyState, hotkey::HotKey};
use std::collections::HashMap;
use tao::event::{Event, StartCause};
use tao::event_loop::{ControlFlow, EventLoopBuilder, EventLoopProxy};
#[cfg(target_os = "macos")]
use tao::platform::macos::{ActivationPolicy, EventLoopExtMacOS};
use tokio::sync::mpsc;
use tray_icon::menu::{CheckMenuItem, Menu, MenuEvent, MenuItem, PredefinedMenuItem, Submenu};
use tray_icon::{Icon, MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};

#[derive(Debug, Clone)]
/// 用途：表示托盘或桌面 UI 发起的 Agent 操作。
pub enum AgentCommand {
    ActivateUi,
    CopyTerminal(TerminalShell),
    SetOutboundMode(OutboundMode),
    ToggleSystemProxy,
    ToggleTun,
    RestartCore,
    #[cfg(windows)]
    SessionEnding(std::sync::mpsc::SyncSender<()>),
    ExitApplication,
}

#[derive(Debug, Clone, Copy)]
/// 用途：枚举可用于写入代理环境变量的终端 Shell。
pub enum TerminalShell {
    PowerShell,
    Cmd,
    Bash,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
/// 用途：表示核心流量的出站代理模式。
pub enum OutboundMode {
    Rule,
    Global,
    Direct,
}

#[derive(Debug, Clone)]
/// 用途：保存托盘图标和菜单所需的运行状态。
pub struct TrayState {
    pub core_running: bool,
    pub system_proxy_enabled: bool,
    pub tun_enabled: bool,
    pub outbound_mode: OutboundMode,
}

#[derive(Debug)]
/// 用途：表示桌面托盘产生的事件。
pub enum DesktopEvent {
    Menu(MenuEvent),
    Tray(TrayIconEvent),
    StateChanged(TrayState),
    CoreRunning(bool),
    HotKey(GlobalHotKeyEvent),
    HotKeysChanged([String; 3]),
    DoubleClickChanged(bool),
    BackendStopped,
}

/// 用途：启动系统托盘事件循环并转发用户操作。
pub fn tray_run(
    start_backend: impl FnOnce(mpsc::UnboundedReceiver<AgentCommand>, EventLoopProxy<DesktopEvent>)
    + Send
    + 'static,
) -> ! {
    let mut event_loop = EventLoopBuilder::<DesktopEvent>::with_user_event().build();
    #[cfg(target_os = "macos")]
    {
        event_loop.set_activation_policy(ActivationPolicy::Accessory);
        event_loop.set_dock_visibility(false);
        event_loop.set_activate_ignoring_other_apps(false);
    }
    let proxy = event_loop.create_proxy();
    let tray_proxy = proxy.clone();
    TrayIconEvent::set_event_handler(Some(move |event| {
        let _ = tray_proxy.send_event(DesktopEvent::Tray(event));
    }));
    let menu_proxy = proxy.clone();
    MenuEvent::set_event_handler(Some(move |event| {
        let _ = menu_proxy.send_event(DesktopEvent::Menu(event));
    }));
    let hotkey_proxy = proxy.clone();
    GlobalHotKeyEvent::set_event_handler(Some(move |event| {
        let _ = hotkey_proxy.send_event(DesktopEvent::HotKey(event));
    }));

    let (command_tx, command_rx) = mpsc::unbounded_channel();
    #[cfg(windows)]
    let _session_watcher = match WindowsSessionWatcher::new(command_tx.clone()) {
        Ok(watcher) => Some(watcher),
        Err(error) => {
            eprintln!("failed to create Windows session watcher: {error:#}");
            None
        }
    };
    let (tray_ready_tx, tray_ready_rx) = std::sync::mpsc::sync_channel(1);
    let backend_proxy = proxy.clone();
    std::thread::spawn(move || {
        if tray_ready_rx.recv().is_ok() {
            start_backend(command_rx, backend_proxy);
        }
    });

    let mut tray = None;
    let mut tray_ready_tx = Some(tray_ready_tx);
    event_loop.run(move |event, _, control_flow| {
        *control_flow = ControlFlow::Wait;
        match event {
            Event::NewEvents(StartCause::Init) => match AgentTray::new() {
                Ok(created) => {
                    tray = Some(created);
                    if let Some(ready) = tray_ready_tx.take() {
                        let _ = ready.send(());
                    }
                }
                Err(error) => {
                    eprintln!("failed to create User Agent tray: {error:#}");
                    *control_flow = ControlFlow::ExitWithCode(1);
                }
            },
            Event::UserEvent(event) => match event {
                DesktopEvent::Menu(event) => {
                    if let Some(tray) = tray.as_ref()
                        && let Some(command) = tray.menu_command(&event)
                    {
                        let _ = command_tx.send(command);
                    }
                }
                DesktopEvent::Tray(event) => {
                    if tray
                        .as_ref()
                        .is_some_and(|tray| tray.is_activation_click(&event))
                    {
                        let _ = command_tx.send(AgentCommand::ActivateUi);
                    }
                }
                DesktopEvent::StateChanged(state) => {
                    if let Some(tray) = tray.as_mut() {
                        tray.set_state(&state);
                    }
                }
                DesktopEvent::CoreRunning(running) => {
                    if let Some(tray) = tray.as_mut() {
                        let mut state = tray.state.clone();
                        state.core_running = running;
                        tray.set_state(&state);
                    }
                }
                DesktopEvent::HotKey(event) => {
                    if event.state == HotKeyState::Pressed
                        && let Some(command) =
                            tray.as_ref().and_then(|tray| tray.hotkey_command(event.id))
                    {
                        let _ = command_tx.send(command);
                    }
                }
                DesktopEvent::HotKeysChanged(gestures) => {
                    if let Some(tray) = tray.as_mut()
                        && let Err(error) = tray.apply_hotkeys(&gestures)
                    {
                        eprintln!("failed to apply global hotkeys: {error:#}");
                    }
                }
                DesktopEvent::DoubleClickChanged(enabled) => {
                    if let Some(tray) = tray.as_mut() {
                        tray.double_click_enabled = enabled;
                    }
                }
                DesktopEvent::BackendStopped => {
                    *control_flow = ControlFlow::Exit;
                }
            },
            _ => {}
        }
    })
}

#[cfg(windows)]
/// 用途：持有用于接收 Windows 会话结束消息的隐藏窗口。
struct WindowsSessionWatcher {
    hwnd: windows_sys::Win32::Foundation::HWND,
}

#[cfg(windows)]
/// 用途：创建并管理 Windows 会话结束消息监听窗口。
impl WindowsSessionWatcher {
    /// 用途：创建隐藏窗口并将会话结束事件转发给 Agent。
    fn new(command_tx: mpsc::UnboundedSender<AgentCommand>) -> Result<Self> {
        use std::ptr;
        use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            CreateWindowExW, RegisterClassW, WNDCLASSW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
            WS_OVERLAPPED,
        };

        let class_name = "SBproxyAgentSessionWatcher\0"
            .encode_utf16()
            .collect::<Vec<_>>();
        let instance = unsafe { GetModuleHandleW(ptr::null()) };
        let window_class = WNDCLASSW {
            lpfnWndProc: Some(windows_session_proc),
            lpszClassName: class_name.as_ptr(),
            hInstance: instance,
            ..unsafe { std::mem::zeroed() }
        };
        unsafe {
            RegisterClassW(&window_class);
        }
        let sender = Box::into_raw(Box::new(command_tx));
        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                class_name.as_ptr(),
                ptr::null(),
                WS_OVERLAPPED,
                0,
                0,
                0,
                0,
                ptr::null_mut(),
                ptr::null_mut(),
                instance,
                sender.cast(),
            )
        };
        if hwnd.is_null() {
            unsafe {
                drop(Box::from_raw(sender));
            }
            return Err(std::io::Error::last_os_error())
                .context("failed to create Windows session watcher");
        }
        Ok(Self { hwnd })
    }
}

#[cfg(windows)]
/// 用途：在监听器释放时销毁 Windows 隐藏窗口。
impl Drop for WindowsSessionWatcher {
    /// 用途：释放 Windows 会话监听窗口的原生资源。
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::DestroyWindow(self.hwnd);
        }
    }
}

#[cfg(windows)]
/// 用途：处理隐藏窗口接收的 Windows 会话生命周期消息。
///
/// 安全性：`lparam` 和窗口用户数据仅按对应 Windows 消息约定解引用，
/// 发送器指针由窗口创建过程分配，并在 `WM_NCDESTROY` 中唯一释放。
unsafe extern "system" fn windows_session_proc(
    hwnd: windows_sys::Win32::Foundation::HWND,
    message: u32,
    wparam: windows_sys::Win32::Foundation::WPARAM,
    lparam: windows_sys::Win32::Foundation::LPARAM,
) -> windows_sys::Win32::Foundation::LRESULT {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CREATESTRUCTW, DefWindowProcW, GetWindowLongPtrW, GWLP_USERDATA, SetWindowLongPtrW,
        WM_ENDSESSION, WM_NCCREATE, WM_NCDESTROY, WM_QUERYENDSESSION,
    };

    if message == WM_NCCREATE {
        let create = unsafe { &*(lparam as *const CREATESTRUCTW) };
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
        }
        return unsafe { DefWindowProcW(hwnd, message, wparam, lparam) };
    }
    let sender = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) }
        as *mut mpsc::UnboundedSender<AgentCommand>;
    if message == WM_QUERYENDSESSION {
        return 1;
    }
    if message == WM_ENDSESSION && wparam != 0 {
        if let Some(sender) = unsafe { sender.as_ref() } {
            let (completed_tx, completed_rx) = std::sync::mpsc::sync_channel(0);
            if sender.send(AgentCommand::SessionEnding(completed_tx)).is_ok() {
                let _ = completed_rx.recv_timeout(std::time::Duration::from_secs(8));
            }
        }
        return 0;
    }
    if message == WM_NCDESTROY && !sender.is_null() {
        let result = unsafe { DefWindowProcW(hwnd, message, wparam, lparam) };
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            drop(Box::from_raw(sender));
        }
        return result;
    }
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}

/// 用途：持有托盘图标、菜单、热键及其当前状态。
struct AgentTray {
    _tray: TrayIcon,
    show: MenuItem,
    copy_power_shell: MenuItem,
    copy_cmd: MenuItem,
    copy_bash: MenuItem,
    outbound_rule: CheckMenuItem,
    outbound_global: CheckMenuItem,
    outbound_direct: CheckMenuItem,
    system_proxy: CheckMenuItem,
    tun: CheckMenuItem,
    restart: MenuItem,
    exit: MenuItem,
    state: TrayState,
    hotkey_manager: GlobalHotKeyManager,
    hotkeys: Vec<HotKey>,
    hotkey_commands: HashMap<u32, AgentCommand>,
    double_click_enabled: bool,
}

/// 用途：提供托盘资源创建、状态同步和事件到命令的映射。
impl AgentTray {
    /// 用途：创建托盘菜单、图标和全局热键管理器。
    fn new() -> Result<Self> {
        let menu = Menu::new();
        let show = MenuItem::new("显示主窗口", true, None);
        let copy_power_shell = MenuItem::new("PowerShell", true, None);
        let copy_cmd = MenuItem::new("CMD", true, None);
        let copy_bash = MenuItem::new("Bash", true, None);
        let copy = Submenu::with_items(
            "复制终端代理命令",
            true,
            &[&copy_power_shell, &copy_cmd, &copy_bash],
        )
        .context("failed to build terminal proxy submenu")?;
        let outbound_rule = CheckMenuItem::new("规则", false, true, None);
        let outbound_global = CheckMenuItem::new("全局", false, false, None);
        let outbound_direct = CheckMenuItem::new("直连", false, false, None);
        let outbound = Submenu::with_items(
            "出站模式",
            false,
            &[&outbound_rule, &outbound_global, &outbound_direct],
        )
        .context("failed to build outbound submenu")?;
        let system_proxy = CheckMenuItem::new("系统代理", false, false, None);
        let tun = CheckMenuItem::new("TUN 模式", true, false, None);
        let restart = MenuItem::new("重启核心", false, None);
        let exit = MenuItem::new("退出", true, None);
        menu.append_items(&[
            &show,
            &PredefinedMenuItem::separator(),
            &copy,
            &PredefinedMenuItem::separator(),
            &outbound,
            &PredefinedMenuItem::separator(),
            &system_proxy,
            &tun,
            &restart,
            &PredefinedMenuItem::separator(),
            &exit,
        ])
        .context("failed to build tray menu")?;

        let tray = TrayIconBuilder::new()
            .with_menu(Box::new(menu))
            .with_tooltip("Shadioium")
            .with_icon(icon_load(false, false, false)?)
            .build()
            .context("failed to build tray icon")?;
        let hotkey_manager =
            GlobalHotKeyManager::new().context("failed to create global hotkey manager")?;
        Ok(Self {
            _tray: tray,
            show,
            copy_power_shell,
            copy_cmd,
            copy_bash,
            outbound_rule,
            outbound_global,
            outbound_direct,
            system_proxy,
            tun,
            restart,
            exit,
            state: TrayState {
                core_running: false,
                system_proxy_enabled: false,
                tun_enabled: false,
                outbound_mode: OutboundMode::Rule,
            },
            hotkey_manager,
            hotkeys: Vec::new(),
            hotkey_commands: HashMap::new(),
            double_click_enabled: true,
        })
    }

    /// 用途：将菜单事件映射为稳定的 Agent 命令。
    fn menu_command(&self, event: &MenuEvent) -> Option<AgentCommand> {
        if event.id == *self.show.id() {
            Some(AgentCommand::ActivateUi)
        } else if event.id == *self.copy_power_shell.id() {
            Some(AgentCommand::CopyTerminal(TerminalShell::PowerShell))
        } else if event.id == *self.copy_cmd.id() {
            Some(AgentCommand::CopyTerminal(TerminalShell::Cmd))
        } else if event.id == *self.copy_bash.id() {
            Some(AgentCommand::CopyTerminal(TerminalShell::Bash))
        } else if event.id == *self.outbound_rule.id() {
            Some(AgentCommand::SetOutboundMode(OutboundMode::Rule))
        } else if event.id == *self.outbound_global.id() {
            Some(AgentCommand::SetOutboundMode(OutboundMode::Global))
        } else if event.id == *self.outbound_direct.id() {
            Some(AgentCommand::SetOutboundMode(OutboundMode::Direct))
        } else if event.id == *self.system_proxy.id() {
            Some(AgentCommand::ToggleSystemProxy)
        } else if event.id == *self.tun.id() {
            Some(AgentCommand::ToggleTun)
        } else if event.id == *self.restart.id() {
            Some(AgentCommand::RestartCore)
        } else if event.id == *self.exit.id() {
            Some(AgentCommand::ExitApplication)
        } else {
            None
        }
    }

    /// 用途：同步托盘菜单、勾选状态和图标。
    fn set_state(&mut self, state: &TrayState) {
        let icon_changed = self.state.core_running != state.core_running
            || self.state.system_proxy_enabled != state.system_proxy_enabled
            || self.state.tun_enabled != state.tun_enabled;
        self.state = state.clone();
        self.restart.set_enabled(state.core_running);
        self.system_proxy.set_enabled(state.core_running);
        self.system_proxy.set_checked(state.system_proxy_enabled);
        self.tun.set_checked(state.tun_enabled);
        self.outbound_rule.set_enabled(state.core_running);
        self.outbound_global.set_enabled(state.core_running);
        self.outbound_direct.set_enabled(state.core_running);
        self.outbound_rule
            .set_checked(state.outbound_mode == OutboundMode::Rule);
        self.outbound_global
            .set_checked(state.outbound_mode == OutboundMode::Global);
        self.outbound_direct
            .set_checked(state.outbound_mode == OutboundMode::Direct);
        if icon_changed
            && let Ok(icon) = icon_load(
                state.core_running,
                state.system_proxy_enabled,
                state.tun_enabled,
            )
        {
            let _ = self._tray.set_icon(Some(icon));
        }
    }

    /// 用途：替换当前注册的全局热键及其命令映射。
    fn apply_hotkeys(&mut self, gestures: &[String; 3]) -> Result<()> {
        if !self.hotkeys.is_empty() {
            self.hotkey_manager
                .unregister_all(&self.hotkeys)
                .context("failed to unregister previous global hotkeys")?;
        }
        self.hotkeys.clear();
        self.hotkey_commands.clear();
        for (gesture, command) in gestures.iter().zip([
            AgentCommand::ActivateUi,
            AgentCommand::ToggleSystemProxy,
            AgentCommand::ToggleTun,
        ]) {
            if gesture.trim().is_empty() {
                continue;
            }
            let hotkey: HotKey = gesture
                .parse()
                .with_context(|| format!("invalid global hotkey: {gesture}"))?;
            self.hotkey_commands.insert(hotkey.id(), command);
            self.hotkeys.push(hotkey);
        }
        if !self.hotkeys.is_empty() {
            self.hotkey_manager
                .register_all(&self.hotkeys)
                .context("failed to register global hotkeys")?;
        }
        Ok(())
    }

    /// 用途：按全局热键标识查找对应 Agent 命令。
    fn hotkey_command(&self, id: u32) -> Option<AgentCommand> {
        self.hotkey_commands.get(&id).cloned()
    }

    /// 用途：判断托盘点击事件是否满足当前激活手势设置。
    fn is_activation_click(&self, event: &TrayIconEvent) -> bool {
        if self.double_click_enabled {
            matches!(
                event,
                TrayIconEvent::DoubleClick {
                    button: MouseButton::Left,
                    ..
                }
            )
        } else {
            matches!(
                event,
                TrayIconEvent::Click {
                    button: MouseButton::Left,
                    button_state: MouseButtonState::Up,
                    ..
                }
            )
        }
    }
}

/// 用途：根据核心、系统代理和 TUN 状态加载对应托盘图标。
fn icon_load(running: bool, proxy: bool, tun: bool) -> Result<Icon> {
    let state = if !running {
        "disabled"
    } else if proxy && tun {
        "proxy_tun_enabled"
    } else if tun {
        "tun_enabled"
    } else if proxy {
        "proxy_enabled"
    } else {
        "disabled"
    };
    #[cfg(target_os = "windows")]
    let bytes: &[u8] = match state {
        "proxy_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/win/tray/proxy_enabled.ico")
        }
        "tun_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/win/tray/tun_enabled.ico")
        }
        "proxy_tun_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/win/tray/proxy_tun_enabled.ico")
        }
        _ => include_bytes!("../../../src/SBproxy.Desktop/Assets/win/tray/disabled.ico"),
    };
    #[cfg(target_os = "macos")]
    let bytes: &[u8] = match state {
        "proxy_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/macos/tray/proxy_enabled.png")
        }
        "tun_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/macos/tray/tun_enabled.png")
        }
        "proxy_tun_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/macos/tray/proxy_tun_enabled.png")
        }
        _ => include_bytes!("../../../src/SBproxy.Desktop/Assets/macos/tray/disabled.png"),
    };
    #[cfg(all(unix, not(target_os = "macos")))]
    let bytes: &[u8] = match state {
        "proxy_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/linux/tray/proxy_enabled.png")
        }
        "tun_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/linux/tray/tun_enabled.png")
        }
        "proxy_tun_enabled" => {
            include_bytes!("../../../src/SBproxy.Desktop/Assets/linux/tray/proxy_tun_enabled.png")
        }
        _ => include_bytes!("../../../src/SBproxy.Desktop/Assets/linux/tray/disabled.png"),
    };

    let image = image::load_from_memory(bytes)
        .context("failed to decode tray icon")?
        .into_rgba8();
    let (width, height) = image.dimensions();
    Icon::from_rgba(image.into_raw(), width, height).context("failed to create tray icon")
}
