use std::path::Path;
use std::process::Command;

use anyhow::{Context, Result, bail};

use crate::settings::AgentSettings;
use crate::storage::file_write_atomic;

/// 用途：根据当前平台启用或停用系统代理。
pub fn proxy_set(user_data_dir: &Path, settings: &AgentSettings, enabled: bool) -> Result<()> {
    #[cfg(target_os = "macos")]
    return proxy_macos_set(user_data_dir, settings, enabled);
    #[cfg(target_os = "linux")]
    return proxy_linux_set(settings, enabled);
    #[cfg(target_os = "windows")]
    return proxy_windows_set(user_data_dir, settings, enabled);
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        let _ = (user_data_dir, settings, enabled);
        bail!("system proxy is not supported on this platform")
    }
}

/// 用途：执行系统代理配置命令并统一处理输出和错误。
fn proxy_command(program: &str, arguments: &[&str]) -> Result<String> {
    let output = Command::new(program)
        .args(arguments)
        .output()
        .with_context(|| format!("failed to start {program}"))?;
    if !output.status.success() {
        let message = String::from_utf8_lossy(&output.stderr).trim().to_owned();
        bail!("{program} failed: {message}");
    }
    Ok(String::from_utf8_lossy(&output.stdout).into_owned())
}

#[cfg(target_os = "macos")]
/// 用途：通过 networksetup 更新 macOS 系统代理。
fn proxy_macos_set(user_data_dir: &Path, settings: &AgentSettings, enabled: bool) -> Result<()> {
    let output = proxy_command("/usr/sbin/networksetup", &["-listallnetworkservices"])?;
    let services = output
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .filter(|line| !line.starts_with("An asterisk"))
        .filter(|line| !line.starts_with('*'))
        .collect::<Vec<_>>();
    for service in services {
        if !enabled {
            for kind in [
                "-setwebproxystate",
                "-setsecurewebproxystate",
                "-setsocksfirewallproxystate",
                "-setautoproxystate",
            ] {
                proxy_command("/usr/sbin/networksetup", &[kind, service, "off"])?;
            }
            continue;
        }
        if settings.is_pac_mode_enabled {
            let pac_path = user_data_dir.join("pac.txt");
            file_write_atomic(&pac_path, settings.pac_script.as_bytes())?;
            let url = format!("file://{}", pac_path.to_string_lossy());
            proxy_command(
                "/usr/sbin/networksetup",
                &["-setautoproxyurl", service, &url],
            )?;
            proxy_command(
                "/usr/sbin/networksetup",
                &["-setautoproxystate", service, "on"],
            )?;
        } else {
            let port = settings.mixed_port.to_string();
            for kind in [
                "-setwebproxy",
                "-setsecurewebproxy",
                "-setsocksfirewallproxy",
            ] {
                proxy_command(
                    "/usr/sbin/networksetup",
                    &[kind, service, &settings.proxy_host, &port],
                )?;
            }
            for kind in [
                "-setwebproxystate",
                "-setsecurewebproxystate",
                "-setsocksfirewallproxystate",
            ] {
                proxy_command("/usr/sbin/networksetup", &[kind, service, "on"])?;
            }
        }
        let bypass = settings
            .system_proxy_bypass
            .split([',', ';'])
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .collect::<Vec<_>>();
        let mut arguments = vec!["-setproxybypassdomains", service];
        if bypass.is_empty() {
            arguments.push("Empty");
        } else {
            arguments.extend(bypass);
        }
        proxy_command("/usr/sbin/networksetup", &arguments)?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
/// 用途：选择桌面环境后更新 Linux 系统代理。
fn proxy_linux_set(settings: &AgentSettings, enabled: bool) -> Result<()> {
    let desktop = std::env::var("XDG_CURRENT_DESKTOP").unwrap_or_default();
    if desktop.to_ascii_uppercase().contains("KDE") {
        return proxy_kde_set(settings, enabled);
    }
    if !enabled {
        proxy_command(
            "gsettings",
            &["set", "org.gnome.system.proxy", "mode", "none"],
        )?;
        return Ok(());
    }
    if settings.is_pac_mode_enabled {
        bail!("PAC mode requires a desktop-specific Linux proxy backend")
    }
    let port = settings.mixed_port.to_string();
    proxy_command(
        "gsettings",
        &["set", "org.gnome.system.proxy", "mode", "manual"],
    )?;
    let ignored = settings
        .system_proxy_bypass
        .split([',', ';'])
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|value| format!("'{}'", value.replace('\'', "\\'")))
        .collect::<Vec<_>>()
        .join(", ");
    proxy_command(
        "gsettings",
        &[
            "set",
            "org.gnome.system.proxy",
            "ignore-hosts",
            &format!("[{ignored}]"),
        ],
    )?;
    for schema in [
        "org.gnome.system.proxy.http",
        "org.gnome.system.proxy.https",
        "org.gnome.system.proxy.socks",
    ] {
        proxy_command("gsettings", &["set", schema, "host", &settings.proxy_host])?;
        proxy_command("gsettings", &["set", schema, "port", &port])?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
/// 用途：通过 KDE 配置命令更新桌面代理。
fn proxy_kde_set(settings: &AgentSettings, enabled: bool) -> Result<()> {
    let writer = if command_is_available("kwriteconfig6") {
        "kwriteconfig6"
    } else if command_is_available("kwriteconfig5") {
        "kwriteconfig5"
    } else {
        bail!("no supported KDE proxy configuration command was found")
    };
    let write = |key: &str, value: &str| {
        proxy_command(
            writer,
            &[
                "--file",
                "kioslaverc",
                "--group",
                "Proxy Settings",
                "--key",
                key,
                value,
            ],
        )
    };
    if !enabled {
        write("ProxyType", "0")?;
    } else {
        if settings.is_pac_mode_enabled {
            bail!("KDE PAC proxy mode is not supported");
        }
        let http = format!("http://{}:{}", settings.proxy_host, settings.mixed_port);
        let socks = format!("socks://{}:{}", settings.proxy_host, settings.mixed_port);
        write("ProxyType", "1")?;
        write("httpProxy", &http)?;
        write("httpsProxy", &http)?;
        write("socksProxy", &socks)?;
        write("NoProxyFor", &settings.system_proxy_bypass)?;
    }
    for qdbus in ["qdbus6", "qdbus-qt6", "qdbus", "qdbus-qt5"] {
        if command_is_available(qdbus) {
            let _ = proxy_command(
                qdbus,
                &[
                    "org.kde.kded6",
                    "/modules/proxyscout",
                    "org.kde.KDEDModule.reparseConfiguration",
                ],
            );
            break;
        }
    }
    Ok(())
}

#[cfg(target_os = "linux")]
/// 用途：判断指定系统命令是否可用。
fn command_is_available(command: &str) -> bool {
    Command::new("which")
        .arg(command)
        .status()
        .is_ok_and(|status| status.success())
}

#[cfg(target_os = "windows")]
/// 用途：通过当前用户注册表更新 Windows 系统代理。
fn proxy_windows_set(user_data_dir: &Path, settings: &AgentSettings, enabled: bool) -> Result<()> {
    let key = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    if !enabled {
        proxy_command(
            "reg",
            &[
                "add",
                key,
                "/v",
                "ProxyEnable",
                "/t",
                "REG_DWORD",
                "/d",
                "0",
                "/f",
            ],
        )?;
        proxy_command("reg", &["delete", key, "/v", "AutoConfigURL", "/f"]).ok();
        proxy_windows_refresh();
        return Ok(());
    }
    if settings.is_pac_mode_enabled {
        let pac_path = user_data_dir.join("pac.txt");
        file_write_atomic(&pac_path, settings.pac_script.as_bytes())?;
        let url = format!("file:///{}", pac_path.to_string_lossy().replace('\\', "/"));
        proxy_command(
            "reg",
            &[
                "add",
                key,
                "/v",
                "AutoConfigURL",
                "/t",
                "REG_SZ",
                "/d",
                &url,
                "/f",
            ],
        )?;
        proxy_command(
            "reg",
            &[
                "add",
                key,
                "/v",
                "ProxyEnable",
                "/t",
                "REG_DWORD",
                "/d",
                "0",
                "/f",
            ],
        )?;
    } else {
        let endpoint = format!("{}:{}", settings.proxy_host, settings.mixed_port);
        proxy_command(
            "reg",
            &[
                "add",
                key,
                "/v",
                "ProxyServer",
                "/t",
                "REG_SZ",
                "/d",
                &endpoint,
                "/f",
            ],
        )?;
        proxy_command(
            "reg",
            &[
                "add",
                key,
                "/v",
                "ProxyOverride",
                "/t",
                "REG_SZ",
                "/d",
                &settings.system_proxy_bypass,
                "/f",
            ],
        )?;
        proxy_command("reg", &["delete", key, "/v", "AutoConfigURL", "/f"]).ok();
        proxy_command(
            "reg",
            &[
                "add",
                key,
                "/v",
                "ProxyEnable",
                "/t",
                "REG_DWORD",
                "/d",
                "1",
                "/f",
            ],
        )?;
    }
    proxy_windows_refresh();
    Ok(())
}

#[cfg(target_os = "windows")]
/// 用途：通知 WinINet 当前用户的代理设置已经变化。
fn proxy_windows_refresh() {
    const INTERNET_OPTION_SETTINGS_CHANGED: u32 = 39;
    const INTERNET_OPTION_REFRESH: u32 = 37;
    #[link(name = "wininet")]
    unsafe extern "system" {
        /// 用途：通知 WinINet 刷新当前用户的互联网设置。
        fn InternetSetOptionW(
            internet: *mut std::ffi::c_void,
            option: u32,
            buffer: *mut std::ffi::c_void,
            buffer_length: u32,
        ) -> i32;
    }
    // 两个 WinINet 通知只刷新当前用户设置，不传入或接管任何原生缓冲区。
    unsafe {
        let _ = InternetSetOptionW(
            std::ptr::null_mut(),
            INTERNET_OPTION_SETTINGS_CHANGED,
            std::ptr::null_mut(),
            0,
        );
        let _ = InternetSetOptionW(
            std::ptr::null_mut(),
            INTERNET_OPTION_REFRESH,
            std::ptr::null_mut(),
            0,
        );
    }
}
