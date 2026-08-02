use std::path::Path;
use std::process::{Command, Stdio};

use anyhow::{Context, Result, bail};
use serde_json::{Value, json};

/// 用途：查询系统服务当前状态并转换为 JSON。
pub fn service_status(service_path: &Path) -> Value {
    if !service_path.is_file() {
        return json!({
            "state": "NotInstalled",
            "message": "Service is not installed.",
        });
    }
    match service_command(service_path, "status", None) {
        Ok(output) if output.starts_with("running") => {
            let values = service_status_parse(&output);
            json!({
                "state": "Running",
                "message": "Service is running.",
                "uptime_seconds": values.get("uptime").and_then(|value| value.parse::<u64>().ok()),
                "heartbeat_seconds": values.get("heartbeat").and_then(|value| value.parse::<u64>().ok()),
                "core_state": values.get("core"),
                "core_pid": values.get("pid").and_then(|value| value.parse::<u32>().ok()),
                "core_last_error": output.split_once(" error=").map(|(_, value)| value),
            })
        }
        Ok(output) if output.starts_with("stopped") => json!({
            "state": "Stopped",
            "message": "Service is installed but not running.",
        }),
        Ok(output) if output.starts_with("not-installed") => json!({
            "state": "NotInstalled",
            "message": "Service is not installed.",
        }),
        Ok(output) => json!({
            "state": "Unknown",
            "message": output,
        }),
        Err(error) => json!({
            "state": "Unknown",
            "message": format!("{error:#}"),
        }),
    }
}

/// 用途：安装或更新系统服务。
pub fn service_install(
    service_update_path: &Path,
    service_path: &Path,
    install_data_dir: &Path,
) -> Result<String> {
    if !service_update_path.is_file() {
        bail!("service executable is missing");
    }
    if service_path.is_file()
        && service_command(service_path, "status", None)
            .is_ok_and(|output| output.starts_with("running"))
    {
        service_command_elevated(service_path, "stop", None)?;
    }
    if let Some(parent) = service_path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::copy(service_update_path, service_path).with_context(|| {
        format!(
            "failed to copy service executable from {} to {}",
            service_update_path.display(),
            service_path.display()
        )
    })?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(service_path, std::fs::Permissions::from_mode(0o755))?;
    }
    service_command_elevated(
        service_path,
        "install",
        if cfg!(target_os = "macos") {
            Some(install_data_dir)
        } else {
            None
        },
    )
}

/// 用途：卸载系统服务及其更新文件。
pub fn service_uninstall(service_path: &Path, service_update_path: &Path) -> Result<String> {
    let command_path = if service_path.is_file() {
        service_path
    } else if service_update_path.is_file() {
        service_update_path
    } else {
        return Ok("Service mode is not installed.".into());
    };
    service_command_elevated(command_path, "uninstall", None)
}

/// 用途：执行不需要提权的服务命令。
pub fn service_command(
    service_path: &Path,
    command: &str,
    input: Option<&str>,
) -> Result<String> {
    let mut process = Command::new(service_path)
        .arg(command)
        .stdin(if input.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .with_context(|| format!("failed to start service command: {command}"))?;
    if let Some(input) = input {
        use std::io::Write;
        process
            .stdin
            .as_mut()
            .context("service command has no standard input")?
            .write_all(input.as_bytes())?;
    }
    let output = process.wait_with_output()?;
    if output.status.success() {
        Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned())
    } else {
        bail!("{}", String::from_utf8_lossy(&output.stderr).trim())
    }
}

/// 用途：解析服务状态命令返回的键值字段。
fn service_status_parse(output: &str) -> std::collections::HashMap<&str, &str> {
    output
        .split_whitespace()
        .filter_map(|part| {
            let (name, value) = part.split_once('=')?;
            Some((name, value.trim_end_matches('s')))
        })
        .collect()
}

#[cfg(windows)]
/// 用途：在 Windows 上以管理员权限执行服务命令。
fn service_command_elevated(
    binary: &Path,
    command: &str,
    argument: Option<&Path>,
) -> Result<String> {
    let mut arguments = format!("'{}'", command.replace('\'', "''"));
    if let Some(argument) = argument {
        arguments.push_str(&format!(
            ",'{}'",
            argument.to_string_lossy().replace('\'', "''")
        ));
    }
    let script = format!(
        "$p=Start-Process -FilePath '{}' -ArgumentList {} -Verb RunAs -Wait -PassThru; exit $p.ExitCode",
        binary.to_string_lossy().replace('\'', "''"),
        arguments
    );
    command_result(
        Command::new("powershell")
            .args(["-NoProfile", "-NonInteractive", "-Command", &script]),
    )
}

#[cfg(target_os = "macos")]
/// 用途：在 macOS 上通过系统授权对话框执行服务命令。
fn service_command_elevated(
    binary: &Path,
    command: &str,
    argument: Option<&Path>,
) -> Result<String> {
    let mut shell = format!(
        "{} {}",
        shell_value_quote(&binary.to_string_lossy()),
        shell_value_quote(command)
    );
    if let Some(argument) = argument {
        shell.push(' ');
        shell.push_str(&shell_value_quote(&argument.to_string_lossy()));
    }
    let script = format!(
        "do shell script \"{}\" with administrator privileges",
        shell.replace('\\', "\\\\").replace('"', "\\\"")
    );
    command_result(Command::new("osascript").args(["-e", &script]))
}

#[cfg(all(unix, not(target_os = "macos")))]
/// 用途：在 Linux 上通过 pkexec 执行服务命令。
fn service_command_elevated(
    binary: &Path,
    command: &str,
    argument: Option<&Path>,
) -> Result<String> {
    let mut process = Command::new("pkexec");
    process.arg(binary).arg(command).args(argument);
    command_result(&mut process)
}

#[cfg(not(any(windows, unix)))]
/// 用途：在不支持的平台上报告服务提权不可用。
fn service_command_elevated(
    _binary: &Path,
    _command: &str,
    _argument: Option<&Path>,
) -> Result<String> {
    bail!("service management is not supported on this platform")
}

/// 用途：执行命令并将成功输出或失败信息统一转换为结果。
fn command_result(command: &mut Command) -> Result<String> {
    let output = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()?;
    if output.status.success() {
        Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned())
    } else {
        bail!("{}", String::from_utf8_lossy(&output.stderr).trim())
    }
}

#[cfg(target_os = "macos")]
/// 用途：将文本转换为可安全嵌入单引号 Shell 参数的形式。
fn shell_value_quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\"'\"'"))
}
