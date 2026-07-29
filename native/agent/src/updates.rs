use std::io::{Cursor, Read};
use std::path::Path;
use std::process::{Command, Stdio};
use std::time::Duration;

use anyhow::{Context, Result, bail};
use reqwest::Client;
use serde_json::{Value, json};
use zip::ZipArchive;

const APP_RELEASES_URL: &str =
    "https://[::]:-1/api/v1/repos/Anonymous/stelliberty-fork/releases?per_page=30";
const APP_LATEST_URL: &str =
    "https://[::]:-1/Anonymous/stelliberty-fork/releases/latest";

pub async fn check_app_update(
    current_version: &str,
    channel: &str,
    user_agent: &str,
) -> Result<Value> {
    let releases: Value = Client::builder()
        .timeout(Duration::from_secs(10))
        .build()?
        .get(APP_RELEASES_URL)
        .header(reqwest::header::USER_AGENT, user_agent)
        .header(reqwest::header::ACCEPT, "application/vnd.github+json")
        .send()
        .await?
        .error_for_status()?
        .json()
        .await?;
    let include_pre_releases = channel.eq_ignore_ascii_case("beta");
    let mut selected: Option<(String, String)> = None;
    for release in releases.as_array().into_iter().flatten() {
        let version = release
            .get("tag_name")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let is_draft = release
            .get("draft")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        let is_pre_release = release
            .get("prerelease")
            .and_then(Value::as_bool)
            .unwrap_or(false)
            || version.contains('-');
        if version.trim().is_empty()
            || is_draft
            || (!include_pre_releases && is_pre_release)
            || compare_versions(version, current_version) <= 0
        {
            continue;
        }
        if selected
            .as_ref()
            .is_none_or(|(current, _)| compare_versions(version, current) > 0)
        {
            selected = Some((
                version.to_owned(),
                release
                    .get("html_url")
                    .and_then(Value::as_str)
                    .unwrap_or(APP_LATEST_URL)
                    .to_owned(),
            ));
        }
    }
    Ok(match selected {
        Some((version, url)) => json!({
            "has_update": true,
            "latest_version": version,
            "message": format!("New version available: {version}"),
            "release_url": url,
            "is_failure": false,
        }),
        None => json!({
            "has_update": false,
            "latest_version": null,
            "message": "You are already on the latest version",
            "release_url": null,
            "is_failure": false,
        }),
    })
}

pub async fn download_latest_core(
    core_path: &Path,
    user_agent: &str,
) -> Result<(String, bool)> {
    let (platform, architecture, extension) = platform_asset();
    let github_prefix = std::env::var("NETPROXY_GITHUB").unwrap_or_default();
    let release_url =
        format!("{github_prefix}https://api.github.com/repos/MetaCubeX/mihomo/releases/latest");
    let client = Client::builder()
        .timeout(Duration::from_secs(120))
        .build()?;
    let release: Value = client
        .get(release_url)
        .header(reqwest::header::USER_AGENT, user_agent)
        .send()
        .await?
        .error_for_status()?
        .json()
        .await?;
    let tag = release
        .get("tag_name")
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .context("release response is missing tag_name")?;
    let latest = normalize_version(tag);
    if let Some(current) = current_core_version(core_path)
        && compare_versions(&current, &latest) >= 0
    {
        return Ok((current, false));
    }
    let asset_name = format!("mihomo-{platform}-{architecture}-{tag}{extension}");
    let asset_url = release
        .get("assets")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .find(|asset| asset.get("name").and_then(Value::as_str) == Some(asset_name.as_str()))
        .and_then(|asset| asset.get("browser_download_url"))
        .and_then(Value::as_str)
        .context("matching core asset was not found")?;
    let asset_url = if asset_url.starts_with("https://github.com/") {
        format!("{github_prefix}{asset_url}")
    } else {
        asset_url.to_owned()
    };
    let archive = client
        .get(asset_url)
        .header(reqwest::header::USER_AGENT, user_agent)
        .send()
        .await?
        .error_for_status()?
        .bytes()
        .await?;
    let core = extract_core(&archive, extension == ".zip")?;
    replace_core(core_path, &core)?;
    Ok((latest, true))
}

pub fn restore_core(core_path: &Path) -> Result<()> {
    let backup = backup_path(core_path);
    if !backup.exists() {
        bail!("core backup does not exist");
    }
    if core_path.exists() {
        std::fs::remove_file(core_path)
            .with_context(|| format!("failed to remove new core: {}", core_path.display()))?;
    }
    std::fs::rename(&backup, core_path)
        .with_context(|| format!("failed to restore core backup: {}", backup.display()))
}

pub fn delete_core_backup(core_path: &Path) -> Result<()> {
    let backup = backup_path(core_path);
    match std::fs::remove_file(&backup) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => {
            Err(error).with_context(|| format!("failed to delete core backup: {}", backup.display()))
        }
    }
}

fn replace_core(core_path: &Path, content: &[u8]) -> Result<()> {
    if content.is_empty() {
        bail!("extracted core file is empty");
    }
    let parent = core_path.parent().context("core path has no parent")?;
    std::fs::create_dir_all(parent)?;
    delete_core_backup(core_path)?;
    if core_path.exists() {
        std::fs::rename(core_path, backup_path(core_path))
            .context("failed to back up the current core")?;
    }
    std::fs::write(core_path, content).context("failed to write the new core")?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(core_path, std::fs::Permissions::from_mode(0o755))
            .context("failed to set core executable permissions")?;
    }
    Ok(())
}

fn extract_core(archive: &[u8], is_zip: bool) -> Result<Vec<u8>> {
    let mut output = Vec::new();
    if is_zip {
        let mut zip = ZipArchive::new(Cursor::new(archive))?;
        let index = (0..zip.len())
            .find(|index| {
                zip.by_index(*index)
                    .ok()
                    .is_some_and(|entry| entry.name().ends_with(".exe") && entry.size() > 0)
            })
            .or_else(|| {
                (0..zip.len()).find(|index| {
                    zip.by_index(*index)
                        .ok()
                        .is_some_and(|entry| entry.size() > 0)
                })
            })
            .context("core archive is empty")?;
        zip.by_index(index)?.read_to_end(&mut output)?;
    } else {
        flate2::read::GzDecoder::new(Cursor::new(archive)).read_to_end(&mut output)?;
    }
    Ok(output)
}

fn current_core_version(core_path: &Path) -> Option<String> {
    if !core_path.is_file() {
        return None;
    }
    let output = Command::new(core_path)
        .arg("-v")
        .stdin(Stdio::null())
        .stderr(Stdio::null())
        .output()
        .ok()?;
    let text = String::from_utf8_lossy(&output.stdout);
    text.split_whitespace()
        .map(|word| word.trim_matches(|character: char| !character.is_ascii_alphanumeric() && character != '.'))
        .find(|word| {
            word.trim_start_matches(['v', 'V'])
                .split('.')
                .filter(|part| !part.is_empty())
                .all(|part| part.chars().all(|character| character.is_ascii_digit()))
                && word.contains('.')
        })
        .map(normalize_version)
}

fn backup_path(core_path: &Path) -> std::path::PathBuf {
    std::path::PathBuf::from(format!("{}.old", core_path.display()))
}

fn platform_asset() -> (&'static str, &'static str, &'static str) {
    let platform = if cfg!(windows) {
        "windows"
    } else if cfg!(target_os = "macos") {
        "darwin"
    } else {
        "linux"
    };
    let architecture = if cfg!(target_arch = "x86_64") {
        "amd64"
    } else if cfg!(target_arch = "aarch64") {
        "arm64"
    } else if cfg!(target_arch = "x86") {
        "386"
    } else {
        "amd64"
    };
    (platform, architecture, if cfg!(windows) { ".zip" } else { ".gz" })
}

fn normalize_version(value: impl AsRef<str>) -> String {
    value
        .as_ref()
        .trim()
        .trim_start_matches(['v', 'V'])
        .to_owned()
}

fn compare_versions(left: &str, right: &str) -> i32 {
    let left = normalize_version(left);
    let right = normalize_version(right);
    let mut left_parts = left.split(['.', '-']);
    let mut right_parts = right.split(['.', '-']);
    loop {
        let left = left_parts.next();
        let right = right_parts.next();
        if left.is_none() && right.is_none() {
            return 0;
        }
        let left_number = left.and_then(|value| value.parse::<i64>().ok()).unwrap_or(0);
        let right_number = right.and_then(|value| value.parse::<i64>().ok()).unwrap_or(0);
        match left_number.cmp(&right_number) {
            std::cmp::Ordering::Less => return -1,
            std::cmp::Ordering::Greater => return 1,
            std::cmp::Ordering::Equal => {}
        }
    }
}
