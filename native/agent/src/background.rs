use std::fs::File;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::Duration;

use anyhow::{Context, Result, bail};
use base64::Engine;
use reqwest::{Client, Method, StatusCode};
use serde_json::{Map, Value};
use time::{OffsetDateTime, format_description::well_known::Rfc3339};
use tokio::sync::{Mutex, broadcast};
use walkdir::WalkDir;
use zip::{CompressionMethod, ZipWriter, write::SimpleFileOptions};

use hub::infra::ipc::MethodHandler;
use hub::infra::ipc::Outgoing;
use std::sync::Arc;

use crate::settings::{AgentSettings, SettingsStore};
use crate::subscriptions::SubscriptionStore;

const RELEASES_API_URL: &str =
    "https://[::]:-1/api/v1/repos/Anonymous/stelliberty-fork/releases?per_page=30";

pub struct BackgroundScheduler {
    user_data_dir: PathBuf,
    app_version: String,
    events: broadcast::Sender<Outgoing>,
    core: Arc<dyn MethodHandler>,
    subscriptions: Arc<Mutex<SubscriptionStore>>,
    first_run: bool,
    next_webdav_check: OffsetDateTime,
    delay_subscription_id: Option<String>,
    delay_interval_minutes: i64,
    next_delay_test: Option<OffsetDateTime>,
}

impl BackgroundScheduler {
    pub fn new(
        user_data_dir: PathBuf,
        app_version: String,
        events: broadcast::Sender<Outgoing>,
        core: Arc<dyn MethodHandler>,
        subscriptions: Arc<Mutex<SubscriptionStore>>,
    ) -> Self {
        Self {
            user_data_dir,
            app_version,
            events,
            core,
            subscriptions,
            first_run: true,
            next_webdav_check: OffsetDateTime::UNIX_EPOCH,
            delay_subscription_id: None,
            delay_interval_minutes: 0,
            next_delay_test: None,
        }
    }

    pub async fn tick(&mut self, settings_store: Arc<Mutex<SettingsStore>>) {
        let settings = match settings_store.lock().await.load() {
            Ok(settings) => settings,
            Err(error) => {
                tracing::warn!("background settings load failed: {error:#}");
                return;
            }
        };
        if let Err(error) = self.update_subscriptions(self.first_run, &settings).await {
            tracing::warn!("subscription scheduler failed: {error:#}");
        }
        if let Err(error) = self.run_auto_delay_if_due(&settings).await {
            tracing::warn!("subscription auto-delay scheduler failed: {error:#}");
        }
        if let Err(error) = self
            .check_app_update_if_due(settings_store.clone(), &settings, self.first_run)
            .await
        {
            tracing::warn!("app update scheduler failed: {error:#}");
        }
        if OffsetDateTime::now_utc() >= self.next_webdav_check {
            self.next_webdav_check = OffsetDateTime::now_utc() + time::Duration::minutes(10);
            if let Err(error) = self
                .backup_webdav_if_due(settings_store.clone(), &settings)
                .await
            {
                tracing::warn!("WebDAV backup scheduler failed: {error:#}");
            }
        }
        self.first_run = false;
    }

    async fn run_auto_delay_if_due(&mut self, settings: &AgentSettings) -> Result<()> {
        let selection_path = self
            .user_data_dir
            .join("subscriptions")
            .join("selection_state.json");
        let selected = std::fs::read_to_string(selection_path)
            .ok()
            .and_then(|content| serde_json::from_str::<Value>(&content).ok())
            .and_then(|document| {
                document
                    .get("CurrentSubscriptionId")
                    .and_then(Value::as_str)
                    .map(str::to_owned)
            });
        let interval = selected
            .as_deref()
            .and_then(|selected| self.subscription_delay_interval(selected).ok())
            .flatten()
            .unwrap_or(0);
        let now = OffsetDateTime::now_utc();
        if selected != self.delay_subscription_id || interval != self.delay_interval_minutes {
            self.delay_subscription_id = selected;
            self.delay_interval_minutes = interval;
            self.next_delay_test =
                (interval > 0).then_some(now + time::Duration::minutes(interval));
            return Ok(());
        }
        if interval <= 0 || self.next_delay_test.is_none_or(|next| now < next) {
            return Ok(());
        }
        self.core
            .handle(
                "core.test_all_delays",
                serde_json::json!({ "url": settings.delay_test_url }),
            )
            .await
            .map_err(|error| anyhow::anyhow!(error.message))?;
        self.next_delay_test = Some(now + time::Duration::minutes(interval));
        Ok(())
    }

    fn subscription_delay_interval(&self, selected: &str) -> Result<Option<i64>> {
        let path = self
            .user_data_dir
            .join("subscriptions")
            .join("subscriptions_list.json");
        let document: Value = serde_json::from_slice(&std::fs::read(path)?)?;
        Ok(document
            .get("Subscriptions")
            .and_then(Value::as_array)
            .and_then(|items| {
                items
                    .iter()
                    .find(|item| item.get("Id").and_then(Value::as_str) == Some(selected))
            })
            .and_then(|item| item.get("AutoTestDelayIntervalMinutes"))
            .and_then(Value::as_i64))
    }

    async fn check_app_update_if_due(
        &self,
        settings_store: Arc<Mutex<SettingsStore>>,
        settings: &AgentSettings,
        startup: bool,
    ) -> Result<()> {
        if !settings.is_auto_check_update_enabled {
            return Ok(());
        }
        let interval = match settings.app_update_check_interval.as_str() {
            "1day" => Some(time::Duration::days(1)),
            "7days" => Some(time::Duration::days(7)),
            "14days" => Some(time::Duration::days(14)),
            _ if startup => None,
            _ => return Ok(()),
        };
        let now = OffsetDateTime::now_utc();
        let last = settings
            .last_app_update_check_time
            .as_deref()
            .and_then(|value| OffsetDateTime::parse(value, &Rfc3339).ok());
        if interval.is_some_and(|interval| last.is_some_and(|last| now - last < interval)) {
            return Ok(());
        }
        let releases: Value = Client::builder()
            .timeout(Duration::from_secs(10))
            .build()?
            .get(RELEASES_API_URL)
            .header(reqwest::header::USER_AGENT, "Shadioium-Agent")
            .send()
            .await?
            .error_for_status()?
            .json()
            .await?;
        settings_store
            .lock()
            .await
            .update_string("LastAppUpdateCheckTime", &format_time(now)?)?;
        let candidate = releases.as_array().and_then(|items| {
            items
                .iter()
                .filter(|item| item.get("draft").and_then(Value::as_bool) != Some(true))
                .filter(|item| {
                    settings.app_update_channel != "stable"
                        || item.get("prerelease").and_then(Value::as_bool) != Some(true)
                })
                .filter_map(|item| {
                    let version = item.get("tag_name")?.as_str()?;
                    version_is_newer(version, &self.app_version).then_some((
                        version.to_owned(),
                        item.get("html_url")
                            .and_then(Value::as_str)
                            .unwrap_or_default()
                            .to_owned(),
                    ))
                })
                .max_by(|left, right| version_key(&left.0).cmp(&version_key(&right.0)))
        });
        if let Some((version, url)) = candidate
            && version != settings.ignored_update_version
        {
            let update_state_path = self.user_data_dir.join("runtime").join("agent-update.json");
            atomic_write(
                &update_state_path,
                &serde_json::to_vec_pretty(&serde_json::json!({ "version": version, "url": url }))?,
            )?;
            let _ = self.events.send(Outgoing::Event {
                event: "app.update_available".into(),
                data: serde_json::json!({ "version": version, "url": url }),
            });
        }
        Ok(())
    }

    async fn update_subscriptions(&self, startup: bool, settings: &AgentSettings) -> Result<()> {
        let subscriptions = self.subscriptions.lock().await.list()?;
        let now = OffsetDateTime::now_utc();
        for subscription in subscriptions {
            let Some(mut item) = subscription.as_object().cloned() else {
                continue;
            };
            if item.get("IsLocalFile").and_then(Value::as_bool) == Some(true)
                || !subscription_is_due(&item, startup, now)
            {
                continue;
            }
            let id = read_string(&item, "Id")?.to_owned();
            match self.download_subscription(&item, settings).await {
                Ok(body) => {
                    validate_subscription_yaml(&body)?;
                    self.subscriptions
                        .lock()
                        .await
                        .save_content(&id, &body)?;
                    match self.apply_updated_subscription(&id, &body).await {
                        Ok(()) => {
                            item.insert("LastUpdatedAt".into(), Value::String(format_time(now)?));
                            item.insert("LastError".into(), Value::Null);
                            item.insert("LastErrorAt".into(), Value::Null);
                        }
                        Err(error) => {
                            item.insert(
                                "LastError".into(),
                                Value::String(format!("updated but failed to apply: {error:#}")),
                            );
                            item.insert("LastErrorAt".into(), Value::String(format_time(now)?));
                        }
                    }
                }
                Err(error) => {
                    item.insert("LastError".into(), Value::String(format!("{error:#}")));
                    item.insert("LastErrorAt".into(), Value::String(format_time(now)?));
                }
            }
            self.subscriptions
                .lock()
                .await
                .update(Value::Object(item))?;
            let _ = self.events.send(Outgoing::Event {
                event: "subscriptions.changed".into(),
                data: serde_json::json!({
                    "kind": "background_update",
                    "subscription_id": id,
                }),
            });
            tracing::info!("subscription updated by User Agent: {id}");
        }
        Ok(())
    }

    async fn apply_updated_subscription(&self, subscription_id: &str, content: &str) -> Result<()> {
        let selected = self
            .subscriptions
            .lock()
            .await
            .current_subscription_id()?;
        if selected.as_deref() != Some(subscription_id) {
            return Ok(());
        }

        let Some(runtime_path) =
            regenerate_runtime_config(&self.user_data_dir, subscription_id, content)?
        else {
            tracing::warn!(
                "selected subscription updated without an existing runtime config: {subscription_id}"
            );
            return Ok(());
        };
        self.core
            .handle(
                "core.apply_config",
                serde_json::json!({
                    "runtime_yaml_path": runtime_path,
                    "subscription_id": subscription_id,
                }),
            )
            .await
            .map_err(|error| anyhow::anyhow!(error.message))?;
        tracing::info!("updated subscription applied by User Agent: {subscription_id}");
        Ok(())
    }

    async fn download_subscription(
        &self,
        item: &Map<String, Value>,
        settings: &AgentSettings,
    ) -> Result<String> {
        if !item
            .get("AgeSecretKey")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .is_empty()
        {
            bail!("encrypted subscription updates require an interactive key operation");
        }
        let mut builder = Client::builder().timeout(Duration::from_secs(10));
        match item
            .get("UpdateProxyMode")
            .and_then(Value::as_str)
            .unwrap_or("Direct")
        {
            "Core" => {
                builder = builder.proxy(reqwest::Proxy::all(format!(
                    "http://{}:{}",
                    settings.proxy_host, settings.mixed_port
                ))?);
            }
            "SystemProxy" => {}
            _ => builder = builder.no_proxy(),
        }
        let user_agent = item
            .get("UserAgent")
            .and_then(Value::as_str)
            .filter(|value| !value.trim().is_empty())
            .unwrap_or("clash.meta");
        let response = builder
            .build()?
            .get(read_string(item, "SourceLocation")?)
            .header(reqwest::header::USER_AGENT, user_agent)
            .header(reqwest::header::CACHE_CONTROL, "no-cache")
            .send()
            .await?
            .error_for_status()?;
        let body = response.text().await?;
        if body.len() > 10 * 1024 * 1024 {
            bail!("subscription response exceeded 10 MiB");
        }
        Ok(body)
    }

    async fn backup_webdav_if_due(
        &self,
        settings_store: Arc<Mutex<SettingsStore>>,
        settings: &AgentSettings,
    ) -> Result<()> {
        if !settings.is_web_dav_backup_enabled || settings.web_dav_url.trim().is_empty() {
            return Ok(());
        }
        let settings_json: Value =
            std::fs::read_to_string(self.user_data_dir.join("settings.json"))
                .ok()
                .and_then(|content| serde_json::from_str(&content).ok())
                .unwrap_or(Value::Null);
        let last = settings_json
            .get("LastWebDavBackupTime")
            .and_then(Value::as_str)
            .and_then(|value| OffsetDateTime::parse(value, &Rfc3339).ok());
        let interval =
            time::Duration::hours(i64::from(settings.web_dav_backup_interval_hours.max(1)));
        let now = OffsetDateTime::now_utc();
        if last.is_some_and(|last| now - last < interval) {
            return Ok(());
        }

        let temporary = std::env::temp_dir().join(format!(
            "sbproxy-agent-backup-{}-{}.zip",
            std::process::id(),
            now.unix_timestamp()
        ));
        create_backup(&self.user_data_dir, &temporary)?;
        let result = upload_webdav(settings, &temporary, now).await;
        let _ = std::fs::remove_file(&temporary);
        result?;
        settings_store
            .lock()
            .await
            .update_string("LastWebDavBackupTime", &format_time(now)?)?;
        tracing::info!("scheduled WebDAV backup completed");
        Ok(())
    }
}

fn regenerate_runtime_config(
    user_data_dir: &Path,
    subscription_id: &str,
    new_content: &str,
) -> Result<Option<PathBuf>> {
    let runtime_dir = user_data_dir.join("runtime").join(subscription_id);
    let original_path = runtime_dir.join("original.yaml");
    let runtime_path = runtime_dir.join("runtime.yaml");
    let (old_original, old_runtime) = match (
        std::fs::read_to_string(&original_path),
        std::fs::read_to_string(&runtime_path),
    ) {
        (Ok(original), Ok(runtime)) => (original, runtime),
        _ => return Ok(None),
    };
    let old_original: serde_yaml_ng::Value =
        serde_yaml_ng::from_str(&old_original).context("stored original YAML is invalid")?;
    let old_runtime: serde_yaml_ng::Value =
        serde_yaml_ng::from_str(&old_runtime).context("stored runtime YAML is invalid")?;
    let new_original: serde_yaml_ng::Value =
        serde_yaml_ng::from_str(new_content).context("updated subscription YAML is invalid")?;
    let new_runtime = carry_runtime_changes(&old_original, &old_runtime, &new_original);
    atomic_write(&original_path, new_content.as_bytes())?;
    atomic_write(
        &runtime_path,
        serde_yaml_ng::to_string(&new_runtime)?.as_bytes(),
    )?;
    Ok(Some(runtime_path))
}

fn carry_runtime_changes(
    old_original: &serde_yaml_ng::Value,
    old_runtime: &serde_yaml_ng::Value,
    new_original: &serde_yaml_ng::Value,
) -> serde_yaml_ng::Value {
    if old_original == old_runtime {
        return new_original.clone();
    }
    let (
        serde_yaml_ng::Value::Mapping(old_original),
        serde_yaml_ng::Value::Mapping(old_runtime),
        serde_yaml_ng::Value::Mapping(new_original),
    ) = (old_original, old_runtime, new_original)
    else {
        return old_runtime.clone();
    };

    let mut merged = new_original.clone();
    for (key, old_value) in old_original {
        match old_runtime.get(key) {
            None => {
                merged.remove(key);
            }
            Some(runtime_value) if runtime_value != old_value => {
                let next_value = merged
                    .get(key)
                    .map(|new_value| carry_runtime_changes(old_value, runtime_value, new_value))
                    .unwrap_or_else(|| runtime_value.clone());
                merged.insert(key.clone(), next_value);
            }
            Some(_) => {}
        }
    }
    for (key, runtime_value) in old_runtime {
        if !old_original.contains_key(key) {
            merged.insert(key.clone(), runtime_value.clone());
        }
    }
    serde_yaml_ng::Value::Mapping(merged)
}

fn subscription_is_due(item: &Map<String, Value>, startup: bool, now: OffsetDateTime) -> bool {
    let mode = item
        .get("AutoUpdateMode")
        .and_then(Value::as_str)
        .unwrap_or("Disabled");
    if startup {
        return mode == "Startup";
    }
    if mode != "Interval" {
        return false;
    }
    let minutes = item
        .get("AutoUpdateIntervalMinutes")
        .and_then(Value::as_i64)
        .unwrap_or(0);
    if minutes <= 0 {
        return false;
    }
    let last = ["LastErrorAt", "LastUpdatedAt"]
        .into_iter()
        .filter_map(|name| item.get(name).and_then(Value::as_str))
        .filter_map(|value| OffsetDateTime::parse(value, &Rfc3339).ok())
        .max();
    last.is_none_or(|last| now - last >= time::Duration::minutes(minutes))
}

fn validate_subscription_yaml(content: &str) -> Result<()> {
    let document: serde_yaml_ng::Value =
        serde_yaml_ng::from_str(content).context("subscription YAML is invalid")?;
    let mapping = document
        .as_mapping()
        .context("subscription YAML root must be a mapping")?;
    if !mapping.contains_key(serde_yaml_ng::Value::String("proxies".into()))
        && !mapping.contains_key(serde_yaml_ng::Value::String("proxy-providers".into()))
    {
        bail!("subscription YAML has no proxies or proxy-providers");
    }
    Ok(())
}

fn create_backup(user_data_dir: &Path, destination: &Path) -> Result<()> {
    let file = File::create(destination)?;
    let mut zip = ZipWriter::new(file);
    let options = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
    let settings_path = user_data_dir.join("settings.json");
    if let Ok(content) = std::fs::read_to_string(&settings_path) {
        let mut settings: Map<String, Value> = serde_json::from_str(&content)?;
        for key in [
            "WindowWidth",
            "WindowHeight",
            "IsWindowMaximized",
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
        ] {
            settings.remove(key);
        }
        zip.start_file("settings.json", options)?;
        zip.write_all(&serde_json::to_vec_pretty(&settings)?)?;
    }
    for entry_name in ["subscriptions", "overrides", "dns.json"] {
        let source = user_data_dir.join(entry_name);
        if !source.exists() {
            continue;
        }
        for entry in WalkDir::new(&source).follow_links(false) {
            let entry = entry?;
            if !entry.file_type().is_file() {
                continue;
            }
            let relative = entry.path().strip_prefix(user_data_dir)?;
            zip.start_file(relative.to_string_lossy().replace('\\', "/"), options)?;
            let mut input = File::open(entry.path())?;
            std::io::copy(&mut input, &mut zip)?;
        }
    }
    zip.finish()?;
    Ok(())
}

async fn upload_webdav(
    settings: &AgentSettings,
    backup_path: &Path,
    now: OffsetDateTime,
) -> Result<()> {
    let client = Client::builder().timeout(Duration::from_secs(30)).build()?;
    let mut directory = settings.web_dav_url.trim_end_matches('/').to_owned();
    for segment in settings
        .web_dav_remote_directory
        .split(['/', '\\'])
        .filter(|segment| !segment.trim().is_empty())
    {
        if matches!(segment, "." | "..") {
            bail!("WebDAV directory contains path traversal");
        }
        directory.push('/');
        directory.push_str(segment);
        let response = authorize(
            client.request(Method::from_bytes(b"MKCOL")?, &directory),
            settings,
        )
        .send()
        .await?;
        if !matches!(
            response.status(),
            StatusCode::CREATED | StatusCode::METHOD_NOT_ALLOWED | StatusCode::OK
        ) {
            bail!("WebDAV MKCOL returned {}", response.status());
        }
    }
    let stamp = now.format(time::macros::format_description!(
        "[year][month][day]-[hour][minute][second]"
    ))?;
    let target = format!("{directory}/shadioium-{stamp}.shadioium");
    let mut bytes = Vec::new();
    File::open(backup_path)?.read_to_end(&mut bytes)?;
    let response = authorize(client.put(target), settings)
        .header(reqwest::header::CONTENT_TYPE, "application/octet-stream")
        .body(bytes)
        .send()
        .await?;
    if !response.status().is_success() {
        bail!("WebDAV upload returned {}", response.status());
    }
    prune_webdav(&client, settings, &directory).await?;
    Ok(())
}

async fn prune_webdav(client: &Client, settings: &AgentSettings, directory: &str) -> Result<()> {
    let response = authorize(
        client.request(Method::from_bytes(b"PROPFIND")?, directory),
        settings,
    )
    .header("Depth", "1")
    .send()
    .await?;
    if !response.status().is_success() {
        bail!("WebDAV PROPFIND returned {}", response.status());
    }
    let body = response.bytes().await?;
    let mut reader = quick_xml::Reader::from_reader(body.as_ref());
    reader.config_mut().trim_text(true);
    let mut hrefs = Vec::new();
    let mut in_href = false;
    loop {
        match reader.read_event()? {
            quick_xml::events::Event::Start(tag) => {
                in_href = tag.local_name().as_ref() == b"href";
            }
            quick_xml::events::Event::Text(text) if in_href => {
                let href = text.decode()?.into_owned();
                if href.ends_with(".shadioium") {
                    hrefs.push(href);
                }
            }
            quick_xml::events::Event::End(tag) if tag.local_name().as_ref() == b"href" => {
                in_href = false;
            }
            quick_xml::events::Event::Eof => break,
            _ => {}
        }
    }
    hrefs.sort_unstable_by(|left, right| right.cmp(left));
    let retention = usize::try_from(settings.web_dav_backup_retention_count.max(1)).unwrap_or(1);
    for href in hrefs.into_iter().skip(retention) {
        let target = if href.starts_with("http://") || href.starts_with("https://") {
            href
        } else {
            let base = reqwest::Url::parse(directory)?;
            base.join(&href)?.to_string()
        };
        let response = authorize(client.delete(target), settings).send().await?;
        if !response.status().is_success() && response.status() != StatusCode::NOT_FOUND {
            bail!("WebDAV DELETE returned {}", response.status());
        }
    }
    Ok(())
}

fn authorize(
    request: reqwest::RequestBuilder,
    settings: &AgentSettings,
) -> reqwest::RequestBuilder {
    if settings.web_dav_user_name.is_empty() {
        request
    } else {
        let token = base64::engine::general_purpose::STANDARD.encode(format!(
            "{}:{}",
            settings.web_dav_user_name, settings.web_dav_password
        ));
        request.header(reqwest::header::AUTHORIZATION, format!("Basic {token}"))
    }
}

fn read_string<'a>(item: &'a Map<String, Value>, name: &str) -> Result<&'a str> {
    item.get(name)
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .with_context(|| format!("subscription is missing {name}"))
}

fn format_time(value: OffsetDateTime) -> Result<String> {
    value.format(&Rfc3339).context("failed to format timestamp")
}

fn atomic_write(path: &Path, content: &[u8]) -> Result<()> {
    let parent = path.parent().context("path has no parent")?;
    std::fs::create_dir_all(parent)?;
    let temporary = path.with_extension("agent.tmp");
    std::fs::write(&temporary, content)?;
    if let Err(error) = std::fs::rename(&temporary, path) {
        if path.exists() {
            std::fs::remove_file(path)?;
            std::fs::rename(&temporary, path)?;
        } else {
            return Err(error.into());
        }
    }
    Ok(())
}

fn version_is_newer(candidate: &str, current: &str) -> bool {
    version_key(candidate) > version_key(current)
}

fn version_key(value: &str) -> Vec<u64> {
    value
        .trim_start_matches(['v', 'V'])
        .split(['.', '-', '+'])
        .map(|part| part.parse().unwrap_or(0))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runtime_changes_are_carried_onto_new_subscription_content() -> Result<()> {
        let old_original: serde_yaml_ng::Value =
            serde_yaml_ng::from_str("proxies: [old]\nmode: rule\ndns:\n  enable: false\n")?;
        let old_runtime: serde_yaml_ng::Value = serde_yaml_ng::from_str(
            "proxies: [old]\nmode: global\nmixed-port: 8888\ndns:\n  enable: false\n  listen: 0.0.0.0:53\n",
        )?;
        let new_original: serde_yaml_ng::Value = serde_yaml_ng::from_str(
            "proxies: [new]\nmode: direct\ndns:\n  enable: true\n  nameserver: [1.1.1.1]\n",
        )?;

        let merged = carry_runtime_changes(&old_original, &old_runtime, &new_original);
        let document = merged
            .as_mapping()
            .context("merged root is not a mapping")?;

        assert_eq!(
            document
                .get(serde_yaml_ng::Value::String("proxies".into()))
                .and_then(serde_yaml_ng::Value::as_sequence)
                .and_then(|items| items.first())
                .and_then(serde_yaml_ng::Value::as_str),
            Some("new")
        );
        assert_eq!(
            document
                .get(serde_yaml_ng::Value::String("mode".into()))
                .and_then(serde_yaml_ng::Value::as_str),
            Some("global")
        );
        assert_eq!(
            document
                .get(serde_yaml_ng::Value::String("mixed-port".into()))
                .and_then(serde_yaml_ng::Value::as_i64),
            Some(8888)
        );
        let dns = document
            .get(serde_yaml_ng::Value::String("dns".into()))
            .and_then(serde_yaml_ng::Value::as_mapping)
            .context("merged DNS is not a mapping")?;
        assert_eq!(
            dns.get(serde_yaml_ng::Value::String("enable".into()))
                .and_then(serde_yaml_ng::Value::as_bool),
            Some(true)
        );
        assert!(dns.contains_key(serde_yaml_ng::Value::String("listen".into())));
        assert!(dns.contains_key(serde_yaml_ng::Value::String("nameserver".into())));
        Ok(())
    }
}
