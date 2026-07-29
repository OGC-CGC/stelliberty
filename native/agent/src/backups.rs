use std::fs::File;
use std::io::{Read, Write};
use std::path::{Component, Path, PathBuf};
use std::time::Duration;

use anyhow::{Context, Result, bail};
use base64::Engine;
use reqwest::{Client, Method, StatusCode};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use time::OffsetDateTime;
use time::format_description::well_known::Rfc2822;
use walkdir::WalkDir;
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipArchive, ZipWriter};

const PORTABLE_ROOTS: &[&str] = &["subscriptions", "overrides"];
const LOCAL_SETTING_KEYS: &[&str] = &[
    "WindowWidth",
    "WindowHeight",
    "IsWindowMaximized",
    "WindowEffect",
    "IsSilentStartEnabled",
    "IsAutoStartEnabled",
    "IsWebDavBackupEnabled",
    "WebDavUrl",
    "WebDavUserName",
    "WebDavPassword",
    "WebDavRemoteDirectory",
    "WebDavBackupIntervalHours",
    "WebDavBackupRetentionCount",
    "LastWebDavBackupTime",
    "LastCoreVersion",
    "LastAppUpdateCheckTime",
    "ProxyHost",
    "SystemProxyBypass",
    "IsPacModeEnabled",
    "PacScript",
    "IsTunEnabled",
    "TunStack",
    "TunDevice",
    "IsTunAutoRouteEnabled",
    "IsTunAutoRedirectEnabled",
    "IsTunAutoDetectInterfaceEnabled",
    "TunDnsHijack",
    "IsTunStrictRouteEnabled",
    "TunRouteExcludeAddresses",
    "IsTunIcmpForwardingDisabled",
    "TunMtu",
];

#[derive(Clone, Deserialize)]
pub struct WebDavSettings {
    pub url: String,
    pub remote_directory: String,
    pub user_name: String,
    pub password: String,
    pub retention_count: i32,
}

pub fn create(user_data_dir: &Path, destination: Option<&str>) -> Result<PathBuf> {
    let path = destination.map_or_else(
        || {
            let timestamp = OffsetDateTime::now_utc()
                .format(time::macros::format_description!(
                    "[year][month][day]-[hour][minute][second]"
                ))
                .unwrap_or_else(|_| "backup".into());
            user_data_dir
                .join("backups")
                .join(format!("shadioium-{timestamp}.shadioium"))
        },
        PathBuf::from,
    );
    let path = absolute_path(&path)?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let file = File::create(&path)?;
    let mut archive = ZipWriter::new(file);
    let options = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
    add_portable_settings(user_data_dir, &mut archive, options)?;
    for name in ["subscriptions", "overrides", "dns.json"] {
        let source = user_data_dir.join(name);
        if source.is_file() {
            archive.start_file(name, options)?;
            std::io::copy(&mut File::open(source)?, &mut archive)?;
            continue;
        }
        if !source.is_dir() {
            continue;
        }
        for entry in WalkDir::new(&source).follow_links(false) {
            let entry = entry?;
            if !entry.file_type().is_file() {
                continue;
            }
            let relative = entry.path().strip_prefix(user_data_dir)?;
            let portable = relative.to_string_lossy().replace('\\', "/");
            if !is_portable_entry(&portable) {
                continue;
            }
            archive.start_file(portable, options)?;
            std::io::copy(&mut File::open(entry.path())?, &mut archive)?;
        }
    }
    archive.finish()?;
    Ok(path)
}

pub fn restore(
    user_data_dir: &Path,
    source: Option<&str>,
    merge: bool,
) -> Result<String> {
    let path = match source {
        Some(path) => PathBuf::from(path),
        None => latest_local_backup(user_data_dir).context("no backup file available to restore")?,
    };
    let local_settings = read_local_settings(user_data_dir);
    let mut archive = ZipArchive::new(File::open(&path)?)?;
    let mut restorable = Vec::new();
    for index in 0..archive.len() {
        let entry = archive.by_index(index)?;
        let name = entry.name().replace('\\', "/");
        if !entry.is_dir() && is_portable_entry(&name) && safe_relative(&name) {
            restorable.push((index, name));
        }
    }
    if restorable.is_empty() {
        bail!("backup file contains no restorable data");
    }
    if !merge {
        for name in ["settings.json", "dns.json"] {
            remove_path(&user_data_dir.join(name))?;
        }
        for name in PORTABLE_ROOTS {
            remove_path(&user_data_dir.join(name))?;
        }
    }
    let mut restored = 0_usize;
    let mut skipped = 0_usize;
    for (index, name) in restorable {
        let destination = user_data_dir.join(&name);
        if merge && destination.exists() && name != "settings.json" {
            skipped += 1;
            continue;
        }
        let mut entry = archive.by_index(index)?;
        let mut bytes = Vec::new();
        entry.read_to_end(&mut bytes)?;
        if name == "settings.json" {
            if merge {
                let (added, ignored) = merge_settings(user_data_dir, &bytes)?;
                restored += added;
                skipped += ignored;
            } else {
                std::fs::write(&destination, bytes)?;
                restored += 1;
            }
            continue;
        }
        if let Some(parent) = destination.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(destination, bytes)?;
        restored += 1;
    }
    if !merge {
        restore_local_settings(user_data_dir, local_settings)?;
    }
    Ok(if merge {
        format!("Merge restore completed: added {restored}, skipped {skipped} existing files")
    } else {
        format!("Overwrite restore completed: restored {restored} files")
    })
}

pub async fn webdav_test(settings: &WebDavSettings, user_agent: &str) -> Result<()> {
    let (client, directory) = ensure_webdav_directory(settings, user_agent).await?;
    let response = authorize(
        client.request(Method::from_bytes(b"PROPFIND")?, directory),
        settings,
    )
    .header("Depth", "0")
    .send()
    .await?;
    expect_status(response.status(), &[StatusCode::MULTI_STATUS, StatusCode::OK])
}

pub async fn webdav_list(
    settings: &WebDavSettings,
    user_agent: &str,
) -> Result<Vec<Value>> {
    let (client, directory) = ensure_webdav_directory(settings, user_agent).await?;
    let response = authorize(
        client.request(Method::from_bytes(b"PROPFIND")?, directory),
        settings,
    )
    .header("Depth", "1")
    .send()
    .await?;
    expect_status(response.status(), &[StatusCode::MULTI_STATUS, StatusCode::OK])?;
    parse_webdav_list(&response.bytes().await?)
}

pub async fn webdav_create(
    user_data_dir: &Path,
    settings: &WebDavSettings,
    user_agent: &str,
) -> Result<()> {
    let (client, directory) = ensure_webdav_directory(settings, user_agent).await?;
    let timestamp = OffsetDateTime::now_utc().format(time::macros::format_description!(
        "[year][month][day]-[hour][minute][second]"
    ))?;
    let file_name = format!("shadioium-{timestamp}.shadioium");
    let temporary = user_data_dir.join("runtime").join(format!(".{file_name}.tmp"));
    create(user_data_dir, temporary.to_str())?;
    let bytes = std::fs::read(&temporary)?;
    let _ = std::fs::remove_file(&temporary);
    let target = file_url(&directory, &file_name)?;
    let response = authorize(client.put(target), settings)
        .header(reqwest::header::CONTENT_TYPE, "application/octet-stream")
        .body(bytes)
        .send()
        .await?;
    expect_status(
        response.status(),
        &[StatusCode::CREATED, StatusCode::NO_CONTENT, StatusCode::OK],
    )?;
    prune_webdav(&client, settings, &directory, user_agent).await
}

pub async fn webdav_restore(
    user_data_dir: &Path,
    settings: &WebDavSettings,
    file_name: Option<&str>,
    merge: bool,
    user_agent: &str,
) -> Result<String> {
    let entries = if file_name.is_none() {
        webdav_list(settings, user_agent).await?
    } else {
        Vec::new()
    };
    let file_name = file_name
        .map(str::to_owned)
        .or_else(|| {
            entries
                .first()
                .and_then(|entry| entry.get("file_name"))
                .and_then(Value::as_str)
                .map(str::to_owned)
        })
        .context("no WebDAV backup file available to restore")?;
    validate_file_name(&file_name)?;
    let (client, directory) = ensure_webdav_directory(settings, user_agent).await?;
    let response = authorize(client.get(file_url(&directory, &file_name)?), settings)
        .send()
        .await?;
    expect_status(response.status(), &[StatusCode::OK])?;
    let temporary = user_data_dir
        .join("runtime")
        .join(format!(".restore-{file_name}.tmp"));
    std::fs::write(&temporary, response.bytes().await?)?;
    let result = restore(user_data_dir, temporary.to_str(), merge);
    let _ = std::fs::remove_file(temporary);
    result
}

pub async fn webdav_delete(
    settings: &WebDavSettings,
    file_name: &str,
    user_agent: &str,
) -> Result<()> {
    validate_file_name(file_name)?;
    let (client, directory) = ensure_webdav_directory(settings, user_agent).await?;
    let response = authorize(
        client.delete(file_url(&directory, file_name)?),
        settings,
    )
    .send()
    .await?;
    expect_status(
        response.status(),
        &[StatusCode::OK, StatusCode::NO_CONTENT, StatusCode::NOT_FOUND],
    )
}

fn add_portable_settings(
    user_data_dir: &Path,
    archive: &mut ZipWriter<File>,
    options: SimpleFileOptions,
) -> Result<()> {
    let Ok(content) = std::fs::read_to_string(user_data_dir.join("settings.json")) else {
        return Ok(());
    };
    let mut settings: Map<String, Value> = serde_json::from_str(&content)?;
    for key in LOCAL_SETTING_KEYS {
        settings.remove(*key);
    }
    archive.start_file("settings.json", options)?;
    archive.write_all(&serde_json::to_vec(&settings)?)?;
    Ok(())
}

fn read_local_settings(user_data_dir: &Path) -> Map<String, Value> {
    let settings: Map<String, Value> = std::fs::read_to_string(user_data_dir.join("settings.json"))
        .ok()
        .and_then(|content| serde_json::from_str(&content).ok())
        .unwrap_or_default();
    settings
        .into_iter()
        .filter(|(key, _)| LOCAL_SETTING_KEYS.contains(&key.as_str()))
        .collect()
}

fn restore_local_settings(user_data_dir: &Path, local: Map<String, Value>) -> Result<()> {
    let path = user_data_dir.join("settings.json");
    let mut settings: Map<String, Value> = std::fs::read_to_string(&path)
        .ok()
        .and_then(|content| serde_json::from_str(&content).ok())
        .unwrap_or_default();
    settings.extend(local);
    std::fs::write(path, serde_json::to_vec(&settings)?)?;
    Ok(())
}

fn merge_settings(user_data_dir: &Path, backup: &[u8]) -> Result<(usize, usize)> {
    let path = user_data_dir.join("settings.json");
    let mut current: Map<String, Value> = std::fs::read_to_string(&path)
        .ok()
        .and_then(|content| serde_json::from_str(&content).ok())
        .unwrap_or_default();
    let backup: Map<String, Value> = serde_json::from_slice(backup)?;
    let mut added = 0;
    let mut ignored = 0;
    for (key, value) in backup {
        if current.contains_key(&key) {
            ignored += 1;
        } else {
            current.insert(key, value);
            added += 1;
        }
    }
    std::fs::write(path, serde_json::to_vec(&current)?)?;
    Ok((added, ignored))
}

async fn ensure_webdav_directory(
    settings: &WebDavSettings,
    user_agent: &str,
) -> Result<(Client, String)> {
    let client = Client::builder()
        .timeout(Duration::from_secs(30))
        .user_agent(user_agent)
        .build()?;
    let mut current = settings.url.trim_end_matches('/').to_owned();
    if !(current.starts_with("http://") || current.starts_with("https://")) {
        bail!("WebDAV URL must be an absolute HTTP or HTTPS URL");
    }
    let directory = if settings.remote_directory.trim().is_empty() {
        "shadioium-backups"
    } else {
        settings.remote_directory.trim()
    };
    for segment in directory
        .split(['/', '\\'])
        .filter(|segment| !segment.is_empty())
    {
        if matches!(segment, "." | "..") {
            bail!("WebDAV remote directory cannot contain path traversal");
        }
        current.push('/');
        current.push_str(segment);
        let response = authorize(
            client.request(Method::from_bytes(b"MKCOL")?, &current),
            settings,
        )
        .send()
        .await?;
        expect_status(
            response.status(),
            &[StatusCode::CREATED, StatusCode::METHOD_NOT_ALLOWED, StatusCode::OK],
        )?;
    }
    Ok((client, current))
}

async fn prune_webdav(
    client: &Client,
    settings: &WebDavSettings,
    directory: &str,
    user_agent: &str,
) -> Result<()> {
    let entries = webdav_list(settings, user_agent).await?;
    let retention = usize::try_from(settings.retention_count.max(1)).unwrap_or(1);
    for entry in entries.into_iter().skip(retention) {
        if let Some(file_name) = entry.get("file_name").and_then(Value::as_str) {
            let response = authorize(
                client.delete(file_url(directory, file_name)?),
                settings,
            )
            .send()
            .await?;
            expect_status(
                response.status(),
                &[StatusCode::OK, StatusCode::NO_CONTENT, StatusCode::NOT_FOUND],
            )?;
        }
    }
    Ok(())
}

fn parse_webdav_list(body: &[u8]) -> Result<Vec<Value>> {
    let mut reader = quick_xml::Reader::from_reader(body);
    reader.config_mut().trim_text(true);
    let mut entries = Vec::new();
    let mut current_field = None;
    let mut href = String::new();
    let mut size: Option<i64> = None;
    let mut modified = None;
    loop {
        match reader.read_event()? {
            quick_xml::events::Event::Start(tag) => {
                let name = tag.local_name();
                current_field = match name.as_ref() {
                    b"href" => Some("href"),
                    b"getcontentlength" => Some("size"),
                    b"getlastmodified" => Some("modified"),
                    _ => None,
                };
            }
            quick_xml::events::Event::Text(text) => match current_field {
                Some("href") => href = text.decode()?.into_owned(),
                Some("size") => size = text.decode()?.parse().ok(),
                Some("modified") => {
                    let value = text.decode()?;
                    modified = OffsetDateTime::parse(&value, &Rfc2822)
                        .ok()
                        .and_then(|value| {
                            value
                                .format(&time::format_description::well_known::Rfc3339)
                                .ok()
                        });
                }
                _ => {}
            },
            quick_xml::events::Event::End(tag) => {
                if tag.local_name().as_ref() == b"response" {
                    let file_name = href
                        .trim_end_matches('/')
                        .rsplit('/')
                        .next()
                        .unwrap_or_default()
                        .to_owned();
                    if file_name.ends_with(".shadioium") {
                        entries.push(json!({
                            "file_name": file_name,
                            "size": size,
                            "last_modified": modified,
                        }));
                    }
                    href.clear();
                    size = None;
                    modified = None;
                }
                current_field = None;
            }
            quick_xml::events::Event::Eof => break,
            _ => {}
        }
    }
    entries.sort_by(|left, right| {
        right
            .get("last_modified")
            .and_then(Value::as_str)
            .cmp(&left.get("last_modified").and_then(Value::as_str))
    });
    Ok(entries)
}

fn latest_local_backup(user_data_dir: &Path) -> Option<PathBuf> {
    let mut files = std::fs::read_dir(user_data_dir.join("backups"))
        .ok()?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.extension().and_then(|value| value.to_str()) == Some("shadioium"))
        .collect::<Vec<_>>();
    files.sort_by_key(|path| {
        std::fs::metadata(path)
            .and_then(|metadata| metadata.modified())
            .ok()
    });
    files.pop()
}

fn is_portable_entry(name: &str) -> bool {
    name == "settings.json"
        || name == "dns.json"
        || (name.starts_with("subscriptions/")
            && (name == "subscriptions/subscriptions_list.json" || name.ends_with(".yaml")))
        || (name.starts_with("overrides/")
            && (name == "overrides/overrides_list.json"
                || name.ends_with(".yaml")
                || name.ends_with(".js")))
}

fn safe_relative(name: &str) -> bool {
    !Path::new(name)
        .components()
        .any(|component| matches!(component, Component::ParentDir | Component::RootDir | Component::Prefix(_)))
}

fn absolute_path(path: &Path) -> Result<PathBuf> {
    if path.is_absolute() {
        Ok(path.to_owned())
    } else {
        Ok(std::env::current_dir()?.join(path))
    }
}

fn remove_path(path: &Path) -> Result<()> {
    if path.is_dir() {
        std::fs::remove_dir_all(path)?;
    } else if path.exists() {
        std::fs::remove_file(path)?;
    }
    Ok(())
}

fn validate_file_name(file_name: &str) -> Result<()> {
    if file_name.is_empty() || file_name.contains(['/', '\\']) {
        bail!("invalid WebDAV backup file name");
    }
    Ok(())
}

fn file_url(directory: &str, file_name: &str) -> Result<String> {
    validate_file_name(file_name)?;
    Ok(format!("{}/{}", directory.trim_end_matches('/'), file_name))
}

fn authorize(
    request: reqwest::RequestBuilder,
    settings: &WebDavSettings,
) -> reqwest::RequestBuilder {
    if settings.user_name.is_empty() {
        request
    } else {
        let token = base64::engine::general_purpose::STANDARD
            .encode(format!("{}:{}", settings.user_name, settings.password));
        request.header(reqwest::header::AUTHORIZATION, format!("Basic {token}"))
    }
}

fn expect_status(actual: StatusCode, expected: &[StatusCode]) -> Result<()> {
    if expected.contains(&actual) {
        Ok(())
    } else {
        bail!("WebDAV request returned {actual}")
    }
}
