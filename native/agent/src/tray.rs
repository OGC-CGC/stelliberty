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
pub enum AgentCommand {
    ActivateUi,
    CopyTerminal(TerminalShell),
    SetOutboundMode(OutboundMode),
    ToggleSystemProxy,
    ToggleTun,
    RestartCore,
    ExitApplication,
}

#[derive(Debug, Clone, Copy)]
pub enum TerminalShell {
    PowerShell,
    Cmd,
    Bash,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OutboundMode {
    Rule,
    Global,
    Direct,
}

#[derive(Debug, Clone)]
pub struct TrayState {
    pub core_running: bool,
    pub system_proxy_enabled: bool,
    pub tun_enabled: bool,
    pub outbound_mode: OutboundMode,
}

#[derive(Debug)]
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

pub fn run(
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
    let backend_proxy = proxy.clone();
    std::thread::spawn(move || start_backend(command_rx, backend_proxy));

    let mut tray = None;
    event_loop.run(move |event, _, control_flow| {
        *control_flow = ControlFlow::Wait;
        match event {
            Event::NewEvents(StartCause::Init) => match AgentTray::new() {
                Ok(created) => tray = Some(created),
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

impl AgentTray {
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
            .with_icon(load_icon(false, false, false)?)
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
            && let Ok(icon) = load_icon(
                state.core_running,
                state.system_proxy_enabled,
                state.tun_enabled,
            )
        {
            let _ = self._tray.set_icon(Some(icon));
        }
    }

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

    fn hotkey_command(&self, id: u32) -> Option<AgentCommand> {
        self.hotkey_commands.get(&id).cloned()
    }

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

fn load_icon(running: bool, proxy: bool, tun: bool) -> Result<Icon> {
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
