use std::path::{Path, PathBuf};
use std::time::Duration;

use anyhow::{Context, Result, bail};
use serde_json::{Map, Value};
use time::{OffsetDateTime, format_description::well_known::Rfc3339};
use tokio::sync::{Mutex, broadcast};

use hub::infra::ipc::MethodHandler;
use hub::infra::ipc::Outgoing;
use std::sync::Arc;

use crate::backups;
use crate::settings::{AgentSettings, SettingsStore};
use crate::sources;
use crate::storage::file_write_atomic;
use crate::subscriptions::SubscriptionStore;
use crate::updates;

/// 用途：调度自动更新、订阅刷新和 WebDAV 备份任务。
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

/// 用途：保存待应用的订阅下载结果及其元数据。
struct SubscriptionDownload {
    content: String,
    traffic_info: Option<Value>,
}

/// 用途：提供后台任务调度器的构造和周期执行接口。
impl BackgroundScheduler {
    /// 用途：创建共享核心与订阅存储的后台任务调度器。
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

    /// 用途：执行一次到期任务检查，并隔离各后台任务的失败。
    pub async fn tick(&mut self, settings_store: Arc<Mutex<SettingsStore>>) {
        let settings = match settings_store.lock().await.load() {
            Ok(settings) => settings,
            Err(error) => {
                tracing::warn!("background settings load failed: {error:#}");
                return;
            }
        };
        if let Err(error) = self.subscription_update_all(self.first_run, &settings).await {
            tracing::warn!("subscription scheduler failed: {error:#}");
        }
        if let Err(error) = self.delay_test_run_if_due(&settings).await {
            tracing::warn!("subscription auto-delay scheduler failed: {error:#}");
        }
        if let Err(error) = self
            .app_update_check_if_due(settings_store.clone(), &settings, self.first_run)
            .await
        {
            tracing::warn!("app update scheduler failed: {error:#}");
        }
        if OffsetDateTime::now_utc() >= self.next_webdav_check {
            self.next_webdav_check = OffsetDateTime::now_utc() + time::Duration::minutes(10);
            if let Err(error) = self
                .webdav_backup_if_due(settings_store.clone(), &settings)
                .await
            {
                tracing::warn!("WebDAV backup scheduler failed: {error:#}");
            }
        }
        self.first_run = false;
    }

    /// 用途：在所选订阅的自动延迟测试到期时触发核心测试。
    async fn delay_test_run_if_due(&mut self, settings: &AgentSettings) -> Result<()> {
        let subscriptions = self.subscriptions.lock().await;
        let selected = subscriptions.current_subscription_id()?;
        let interval = selected
            .as_deref()
            .and_then(|selected| {
                subscriptions.list().ok()?.into_iter().find_map(|item| {
                    (item.get("Id").and_then(Value::as_str) == Some(selected))
                        .then(|| {
                            item.get("AutoTestDelayIntervalMinutes")
                                .and_then(Value::as_i64)
                        })
                        .flatten()
                })
            })
            .unwrap_or(0);
        drop(subscriptions);
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

    /// 用途：按设置的启动或间隔策略检查应用更新。
    async fn app_update_check_if_due(
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
        let result = updates::app_update_check(
            &self.app_version,
            &settings.app_update_channel,
            "Shadioium-Agent",
        )
        .await?;
        settings_store
            .lock()
            .await
            .update_string("LastAppUpdateCheckTime", &time_format(now)?)?;
        if result.get("has_update").and_then(Value::as_bool) == Some(true)
            && let Some(version) = result.get("latest_version").and_then(Value::as_str)
            && version != settings.ignored_update_version
        {
            let url = result
                .get("release_url")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let update_state_path = self.user_data_dir.join("runtime").join("agent-update.json");
            file_write_atomic(
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

    /// 用途：下载并应用所有满足自动更新条件的订阅。
    async fn subscription_update_all(
        &self,
        startup: bool,
        settings: &AgentSettings,
    ) -> Result<()> {
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
            let id = field_string(&item, "Id")?.to_owned();
            match self.subscription_download(&item, settings).await {
                Ok(download) => {
                    let body = download.content;
                    subscription_yaml_validate(&body)?;
                    item.insert("SourceFormat".into(), Value::from(0));
                    item.insert(
                        "TrafficInfo".into(),
                        download.traffic_info.unwrap_or(Value::Null),
                    );
                    item.insert(
                        "BuiltinChainProxyNames".into(),
                        Value::Array(
                            chain_proxy_names_builtin(&body)?
                                .into_iter()
                                .map(Value::String)
                                .collect(),
                        ),
                    );
                    self.subscriptions
                        .lock()
                        .await
                        .save_content(&id, &body)?;
                    match self.subscription_apply(&id, &body).await {
                        Ok(()) => {
                            item.insert("LastUpdatedAt".into(), Value::String(time_format(now)?));
                            item.insert("LastError".into(), Value::Null);
                            item.insert("LastErrorAt".into(), Value::Null);
                        }
                        Err(error) => {
                            item.insert(
                                "LastError".into(),
                                Value::String(format!("updated but failed to apply: {error:#}")),
                            );
                            item.insert("LastErrorAt".into(), Value::String(time_format(now)?));
                        }
                    }
                }
                Err(error) => {
                    item.insert("LastError".into(), Value::String(format!("{error:#}")));
                    item.insert("LastErrorAt".into(), Value::String(time_format(now)?));
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

    /// 用途：为当前订阅重建运行时配置并请求核心应用。
    async fn subscription_apply(&self, subscription_id: &str, content: &str) -> Result<()> {
        let selected = self
            .subscriptions
            .lock()
            .await
            .current_subscription_id()?;
        if selected.as_deref() != Some(subscription_id) {
            return Ok(());
        }

        let Some(runtime_path) =
            runtime_config_regenerate(&self.user_data_dir, subscription_id, content)?
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

    /// 用途：下载、解密并整理单个订阅及其流量元数据。
    async fn subscription_download(
        &self,
        item: &Map<String, Value>,
        settings: &AgentSettings,
    ) -> Result<SubscriptionDownload> {
        let age_secret_key = item
            .get("AgeSecretKey")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let proxy_mode = item
            .get("UpdateProxyMode")
            .and_then(Value::as_str)
            .unwrap_or("Direct");
        let user_agent = item
            .get("UserAgent")
            .and_then(Value::as_str)
            .filter(|value| !value.trim().is_empty())
            .unwrap_or("clash.meta");
        let downloaded = sources::source_download_raw(
            field_string(item, "SourceLocation")?,
            user_agent,
            proxy_mode,
            settings,
            Duration::from_secs(10),
        )
        .await?;
        let traffic_info =
            downloaded
                .traffic_info
                .map(|[upload, download, total, expire]| {
                    serde_json::json!({
                        "Upload": upload,
                        "Download": download,
                        "Total": total,
                        "Expire": expire,
                    })
                });
        let body = hub::capabilities::age_text::decrypt_text(&downloaded.content, age_secret_key)
            .context("failed to decrypt age subscription")?;
        if body.len() > 10 * 1024 * 1024 {
            bail!("decrypted subscription exceeded 10 MiB");
        }
        Ok(SubscriptionDownload {
            content: body,
            traffic_info,
        })
    }

    /// 用途：在备份间隔到期时创建 WebDAV 备份并记录完成时间。
    async fn webdav_backup_if_due(
        &self,
        settings_store: Arc<Mutex<SettingsStore>>,
        settings: &AgentSettings,
    ) -> Result<()> {
        if !settings.is_web_dav_backup_enabled || settings.web_dav_url.trim().is_empty() {
            return Ok(());
        }
        let last = settings
            .last_web_dav_backup_time
            .as_deref()
            .and_then(|value| OffsetDateTime::parse(value, &Rfc3339).ok());
        let interval =
            time::Duration::hours(i64::from(settings.web_dav_backup_interval_hours.max(1)));
        let now = OffsetDateTime::now_utc();
        if last.is_some_and(|last| now - last < interval) {
            return Ok(());
        }

        let webdav_settings = backups::WebDavSettings {
            url: settings.web_dav_url.clone(),
            remote_directory: settings.web_dav_remote_directory.clone(),
            user_name: settings.web_dav_user_name.clone(),
            password: settings.web_dav_password.clone(),
            retention_count: settings.web_dav_backup_retention_count,
        };
        backups::webdav_create(
            &self.user_data_dir,
            &webdav_settings,
            &format!("Shadioium/{}", self.app_version),
        )
        .await?;
        settings_store
            .lock()
            .await
            .update_string("LastWebDavBackupTime", &time_format(now)?)?;
        tracing::info!("scheduled WebDAV backup completed");
        Ok(())
    }
}

/// 用途：基于新订阅内容重建现有运行时配置并保留用户修改。
fn runtime_config_regenerate(
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
    let new_runtime = runtime_changes_carry(&old_original, &old_runtime, &new_original);
    file_write_atomic(&original_path, new_content.as_bytes())?;
    file_write_atomic(
        &runtime_path,
        serde_yaml_ng::to_string(&new_runtime)?.as_bytes(),
    )?;
    Ok(Some(runtime_path))
}

/// 用途：将旧运行时相对原始配置的差异递归迁移到新配置。
fn runtime_changes_carry(
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
            .map(|new_value| runtime_changes_carry(old_value, runtime_value, new_value))
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

/// 用途：判断订阅是否满足启动或间隔自动更新条件。
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

/// 用途：校验订阅 YAML 根结构及必要代理字段。
fn subscription_yaml_validate(content: &str) -> Result<()> {
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

/// 用途：提取配置中使用内置链式拨号的代理名称。
fn chain_proxy_names_builtin(content: &str) -> Result<Vec<String>> {
    let document: serde_yaml_ng::Value =
        serde_yaml_ng::from_str(content).context("subscription YAML is invalid")?;
    let Some(proxies) = document
        .as_mapping()
        .and_then(|root| root.get(serde_yaml_ng::Value::String("proxies".into())))
        .and_then(serde_yaml_ng::Value::as_sequence)
    else {
        return Ok(Vec::new());
    };
    let mut names = Vec::new();
    for proxy in proxies {
        let Some(proxy) = proxy.as_mapping() else {
            continue;
        };
        let has_dialer = proxy
            .get(serde_yaml_ng::Value::String("dialer-proxy".into()))
            .and_then(serde_yaml_ng::Value::as_str)
            .is_some_and(|value| !value.trim().is_empty());
        let name = proxy
            .get(serde_yaml_ng::Value::String("name".into()))
            .and_then(serde_yaml_ng::Value::as_str)
            .filter(|value| !value.trim().is_empty());
        if has_dialer
            && let Some(name) = name
            && !names.iter().any(|existing| existing == name)
        {
            names.push(name.to_owned());
        }
    }
    Ok(names)
}

/// 用途：读取订阅对象中必需且非空的字符串字段。
fn field_string<'a>(item: &'a Map<String, Value>, name: &str) -> Result<&'a str> {
    item.get(name)
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .with_context(|| format!("subscription is missing {name}"))
}

/// 用途：将时间统一格式化为 RFC 3339 文本。
fn time_format(value: OffsetDateTime) -> Result<String> {
    value.format(&Rfc3339).context("failed to format timestamp")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    /// 用途：验证运行时差异能够迁移到新的订阅配置。
    fn runtime_changes_are_carried_onto_new_subscription_content() -> Result<()> {
        let old_original: serde_yaml_ng::Value =
            serde_yaml_ng::from_str("proxies: [old]\nmode: rule\ndns:\n  enable: false\n")?;
        let old_runtime: serde_yaml_ng::Value = serde_yaml_ng::from_str(
            "proxies: [old]\nmode: global\nmixed-port: 8888\ndns:\n  enable: false\n  listen: 0.0.0.0:53\n",
        )?;
        let new_original: serde_yaml_ng::Value = serde_yaml_ng::from_str(
            "proxies: [new]\nmode: direct\ndns:\n  enable: true\n  nameserver: [1.1.1.1]\n",
        )?;

        let merged = runtime_changes_carry(&old_original, &old_runtime, &new_original);
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
