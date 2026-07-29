use std::path::Path;
use std::process::{Command, Stdio};

use anyhow::{Context, Result, bail};
use serde_json::{Value, json};

pub fn status(service_path: &Path) -> Value {
    if !service_path.is_file() {
        return json!({
            "state": "NotInstalled",
            "message": "Service is not installed.",
        });
    }
    match direct_command(service_path, "status", None, None) {
        Ok(output) if output.starts_with("running") => {
            let values = parse_status(&output);
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

pub fn install(
    service_update_path: &Path,
    service_path: &Path,
    install_data_dir: &Path,
) -> Result<String> {
    if !service_update_path.is_file() {
        bail!("service executable is missing");
    }
    if service_path.is_file()
        && direct_command(service_path, "status", None, None)
            .is_ok_and(|output| output.starts_with("running"))
    {
        elevated_command(service_path, "stop", None)?;
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
    elevated_command(
        service_path,
        "install",
        if cfg!(target_os = "macos") {
            Some(install_data_dir)
        } else {
            None
        },
    )
}

pub fn uninstall(service_path: &Path, service_update_path: &Path) -> Result<String> {
    let command_path = if service_path.is_file() {
        service_path
    } else if service_update_path.is_file() {
        service_update_path
    } else {
        return Ok("Service mode is not installed.".into());
    };
    elevated_command(command_path, "uninstall", None)
}

pub fn direct_command(
    service_path: &Path,
    command: &str,
    argument: Option<&Path>,
    input: Option<&str>,
) -> Result<String> {
    let mut process = Command::new(service_path)
        .arg(command)
        .args(argument)
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

fn parse_status(output: &str) -> std::collections::HashMap<&str, &str> {
    output
        .split_whitespace()
        .filter_map(|part| {
            let (name, value) = part.split_once('=')?;
            Some((name, value.trim_end_matches('s')))
        })
        .collect()
}

#[cfg(windows)]
fn elevated_command(binary: &Path, command: &str, argument: Option<&Path>) -> Result<String> {
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
    command_output(
        Command::new("powershell")
            .args(["-NoProfile", "-NonInteractive", "-Command", &script]),
    )
}

#[cfg(target_os = "macos")]
fn elevated_command(binary: &Path, command: &str, argument: Option<&Path>) -> Result<String> {
    let mut shell = format!(
        "{} {}",
        shell_quote(&binary.to_string_lossy()),
        shell_quote(command)
    );
    if let Some(argument) = argument {
        shell.push(' ');
        shell.push_str(&shell_quote(&argument.to_string_lossy()));
    }
    let script = format!(
        "do shell script \"{}\" with administrator privileges",
        shell.replace('\\', "\\\\").replace('"', "\\\"")
    );
    command_output(Command::new("osascript").args(["-e", &script]))
}

#[cfg(all(unix, not(target_os = "macos")))]
fn elevated_command(binary: &Path, command: &str, argument: Option<&Path>) -> Result<String> {
    let mut process = Command::new("pkexec");
    process.arg(binary).arg(command).args(argument);
    command_output(&mut process)
}

#[cfg(not(any(windows, unix)))]
fn elevated_command(_binary: &Path, _command: &str, _argument: Option<&Path>) -> Result<String> {
    bail!("service management is not supported on this platform")
}

fn command_output(command: &mut Command) -> Result<String> {
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
fn shell_quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\"'\"'"))
}
