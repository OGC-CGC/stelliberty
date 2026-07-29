use std::path::{Path, PathBuf};
use std::time::Duration;

use anyhow::{Context, Result, bail};
use reqwest::Client;
use serde_json::{Value, json};

use crate::settings::AgentSettings;

const MAX_DOWNLOAD_BYTES: usize = 10 * 1024 * 1024;

pub async fn download(
    source_location: &str,
    user_agent: &str,
    proxy_mode: &str,
    settings: &AgentSettings,
) -> Result<Value> {
    let mut builder = Client::builder().timeout(Duration::from_secs(30));
    match proxy_mode {
        "Core" => {
            builder = builder.proxy(reqwest::Proxy::all(format!(
                "http://{}:{}",
                settings.proxy_host, settings.mixed_port
            ))?);
        }
        "SystemProxy" => {}
        _ => builder = builder.no_proxy(),
    }
    let response = builder
        .build()?
        .get(source_location)
        .header(
            reqwest::header::USER_AGENT,
            if user_agent.trim().is_empty() {
                "clash.meta"
            } else {
                user_agent
            },
        )
        .header(reqwest::header::CACHE_CONTROL, "no-cache")
        .send()
        .await?
        .error_for_status()?;
    if response
        .content_length()
        .is_some_and(|length| length > MAX_DOWNLOAD_BYTES as u64)
    {
        bail!("download response exceeded 10 MiB");
    }
    let traffic_info = response
        .headers()
        .get("subscription-userinfo")
        .and_then(|value| value.to_str().ok())
        .map(parse_traffic_info);
    let bytes = response.bytes().await?;
    if bytes.len() > MAX_DOWNLOAD_BYTES {
        bail!("download response exceeded 10 MiB");
    }
    let content = String::from_utf8(bytes.to_vec()).context("download response is not UTF-8")?;
    Ok(json!({
        "content": content,
        "traffic_info": traffic_info,
    }))
}

pub fn read_local(path: &str) -> Result<String> {
    std::fs::read_to_string(path).with_context(|| format!("failed to read local file: {path}"))
}

pub fn upload_provider(
    core_dir: &Path,
    vehicle_type: &str,
    provider_path: &str,
    source_path: &str,
) -> Result<Value> {
    if !vehicle_type.eq_ignore_ascii_case("File") {
        return Ok(json!({
            "uploaded": false,
            "skip_reason": "provider is not File type",
        }));
    }
    let source = PathBuf::from(source_path);
    if !source.is_file() {
        return Ok(json!({
            "uploaded": false,
            "skip_reason": "source file does not exist",
        }));
    }
    let relative = provider_path.strip_prefix("./").unwrap_or(provider_path);
    let target = if Path::new(provider_path).is_absolute() {
        PathBuf::from(provider_path)
    } else {
        core_dir.join(relative)
    };
    let parent = target
        .parent()
        .context("provider target path has no parent directory")?;
    std::fs::create_dir_all(parent)
        .with_context(|| format!("failed to create provider directory: {}", parent.display()))?;
    let root = std::fs::canonicalize(core_dir)
        .with_context(|| format!("failed to resolve core directory: {}", core_dir.display()))?;
    let resolved_parent = std::fs::canonicalize(parent).with_context(|| {
        format!(
            "failed to resolve provider target directory: {}",
            parent.display()
        )
    })?;
    if !resolved_parent.starts_with(&root) {
        bail!("provider target is outside the core directory");
    }
    std::fs::copy(&source, &target).with_context(|| {
        format!(
            "failed to copy provider file from {} to {}",
            source.display(),
            target.display()
        )
    })?;
    Ok(json!({
        "uploaded": true,
        "skip_reason": null,
    }))
}

fn parse_traffic_info(header: &str) -> Value {
    let mut upload = 0_i64;
    let mut download = 0_i64;
    let mut total = 0_i64;
    let mut expire = 0_i64;
    for part in header.split(';') {
        let Some((name, value)) = part.trim().split_once('=') else {
            continue;
        };
        let Ok(value) = value.trim().parse::<i64>() else {
            continue;
        };
        match name.trim().to_ascii_lowercase().as_str() {
            "upload" => upload = value,
            "download" => download = value,
            "total" => total = value,
            "expire" => expire = value,
            _ => {}
        }
    }
    json!({
        "upload": upload,
        "download": download,
        "total": total,
        "expire": expire,
    })
}
