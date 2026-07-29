use std::path::Path;
use std::process::Command;

use anyhow::{Context, Result, bail};

use crate::settings::AgentSettings;

pub fn set_enabled(user_data_dir: &Path, settings: &AgentSettings, enabled: bool) -> Result<()> {
    #[cfg(target_os = "macos")]
    return set_macos(user_data_dir, settings, enabled);
    #[cfg(target_os = "linux")]
    return set_linux(settings, enabled);
    #[cfg(target_os = "windows")]
    return set_windows(user_data_dir, settings, enabled);
    #[cfg(not(any(target_os = "macos", target_os = "linux", target_os = "windows")))]
    {
        let _ = (user_data_dir, settings, enabled);
        bail!("system proxy is not supported on this platform")
    }
}

fn run(program: &str, arguments: &[&str]) -> Result<String> {
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
fn set_macos(user_data_dir: &Path, settings: &AgentSettings, enabled: bool) -> Result<()> {
    let output = run("/usr/sbin/networksetup", &["-listallnetworkservices"])?;
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
                run("/usr/sbin/networksetup", &[kind, service, "off"])?;
            }
            continue;
        }
        if settings.is_pac_mode_enabled {
            let pac_path = user_data_dir.join("pac.txt");
            std::fs::write(&pac_path, &settings.pac_script)?;
            let url = format!("file://{}", pac_path.to_string_lossy());
            run(
                "/usr/sbin/networksetup",
                &["-setautoproxyurl", service, &url],
            )?;
            run(
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
                run(
                    "/usr/sbin/networksetup",
                    &[kind, service, &settings.proxy_host, &port],
                )?;
            }
            for kind in [
                "-setwebproxystate",
                "-setsecurewebproxystate",
                "-setsocksfirewallproxystate",
            ] {
                run("/usr/sbin/networksetup", &[kind, service, "on"])?;
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
        run("/usr/sbin/networksetup", &arguments)?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
fn set_linux(settings: &AgentSettings, enabled: bool) -> Result<()> {
    let desktop = std::env::var("XDG_CURRENT_DESKTOP").unwrap_or_default();
    if desktop.to_ascii_uppercase().contains("KDE") {
        return set_kde(settings, enabled);
    }
    if !enabled {
        run(
            "gsettings",
            &["set", "org.gnome.system.proxy", "mode", "none"],
        )?;
        return Ok(());
    }
    if settings.is_pac_mode_enabled {
        bail!("PAC mode requires a desktop-specific Linux proxy backend")
    }
    let port = settings.mixed_port.to_string();
    run(
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
    run(
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
        run("gsettings", &["set", schema, "host", &settings.proxy_host])?;
        run("gsettings", &["set", schema, "port", &port])?;
    }
    Ok(())
}

#[cfg(target_os = "linux")]
fn set_kde(settings: &AgentSettings, enabled: bool) -> Result<()> {
    let writer = if command_exists("kwriteconfig6") {
        "kwriteconfig6"
    } else if command_exists("kwriteconfig5") {
        "kwriteconfig5"
    } else {
        bail!("no supported KDE proxy configuration command was found")
    };
    let write = |key: &str, value: &str| {
        run(
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
        if command_exists(qdbus) {
            let _ = run(
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
fn command_exists(command: &str) -> bool {
    Command::new("which")
        .arg(command)
        .status()
        .is_ok_and(|status| status.success())
}

#[cfg(target_os = "windows")]
fn set_windows(user_data_dir: &Path, settings: &AgentSettings, enabled: bool) -> Result<()> {
    let key = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    if !enabled {
        run(
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
        run("reg", &["delete", key, "/v", "AutoConfigURL", "/f"]).ok();
        refresh_windows_proxy();
        return Ok(());
    }
    if settings.is_pac_mode_enabled {
        let pac_path = user_data_dir.join("pac.txt");
        std::fs::write(&pac_path, &settings.pac_script)?;
        let url = format!("file:///{}", pac_path.to_string_lossy().replace('\\', "/"));
        run(
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
        run(
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
        run(
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
        run(
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
        run("reg", &["delete", key, "/v", "AutoConfigURL", "/f"]).ok();
        run(
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
    refresh_windows_proxy();
    Ok(())
}

#[cfg(target_os = "windows")]
fn refresh_windows_proxy() {
    const INTERNET_OPTION_SETTINGS_CHANGED: u32 = 39;
    const INTERNET_OPTION_REFRESH: u32 = 37;
    #[link(name = "wininet")]
    unsafe extern "system" {
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
