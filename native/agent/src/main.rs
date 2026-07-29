use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result, bail};
use fs2::FileExt;
use hub::infra::core_api::CoreApiClient;
use hub::infra::core_runtime::CoreRuntime;
use hub::infra::ipc::{ErrorBody, IpcServer, MethodHandler, Outgoing};
use hub::infra::paths::HubPaths;
use serde::{Deserialize, Serialize};
use tokio::sync::{Mutex, broadcast, mpsc};

mod background;
mod backups;
mod overrides;
mod runtime_files;
mod settings;
mod service_manager;
mod sources;
mod subscriptions;
mod system_proxy;
mod tray;
mod updates;

const PROTOCOL_VERSION: u32 = 2;

#[derive(Debug)]
struct AgentOptions {
    endpoint: String,
    mihomo_path: PathBuf,
    data_core_dir: PathBuf,
    user_data_dir: PathBuf,
    mihomo_endpoint: String,
    bootstrap_yaml_path: PathBuf,
    ui_path: PathBuf,
    service_path: PathBuf,
    service_update_path: PathBuf,
    install_data_dir: PathBuf,
    app_version: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
struct AgentState {
    desired_running: bool,
    runtime_yaml_path: Option<PathBuf>,
    system_proxy_enabled: bool,
}

impl Default for AgentState {
    fn default() -> Self {
        Self {
            desired_running: true,
            runtime_yaml_path: None,
            system_proxy_enabled: false,
        }
    }
}

struct AgentStateStore {
    path: PathBuf,
}

impl AgentStateStore {
    fn new(user_data_dir: &Path) -> Self {
        Self {
            path: user_data_dir.join("runtime").join("agent-state.json"),
        }
    }

    fn load(&self) -> AgentState {
        let Ok(content) = std::fs::read_to_string(&self.path) else {
            return AgentState::default();
        };
        match serde_json::from_str(&content) {
            Ok(state) => state,
            Err(error) => {
                tracing::warn!("agent state is invalid; defaults will be used: {error}");
                AgentState::default()
            }
        }
    }

    fn save(&self, state: &AgentState) -> Result<()> {
        let parent = self
            .path
            .parent()
            .context("agent state path has no parent directory")?;
        std::fs::create_dir_all(parent).context("failed to create agent state directory")?;
        let temporary_path = self.path.with_extension("json.tmp");
        let content =
            serde_json::to_vec_pretty(state).context("failed to serialize agent state")?;
        std::fs::write(&temporary_path, content)
            .context("failed to write temporary agent state")?;
        if let Err(error) = std::fs::rename(&temporary_path, &self.path) {
            if self.path.exists() {
                std::fs::remove_file(&self.path)
                    .context("failed to replace previous agent state")?;
                std::fs::rename(&temporary_path, &self.path)
                    .context("failed to install new agent state")?;
            } else {
                return Err(error).context("failed to install new agent state");
            }
        }
        Ok(())
    }
}

struct AgentHandler {
    core: Arc<CoreRuntime>,
    state: Arc<Mutex<AgentState>>,
    state_store: Arc<AgentStateStore>,
    shutdown_tx: mpsc::Sender<()>,
    events: broadcast::Sender<Outgoing>,
    ui: Mutex<UiState>,
    settings: Arc<Mutex<settings::SettingsStore>>,
    subscriptions: Arc<Mutex<subscriptions::SubscriptionStore>>,
    overrides: Arc<Mutex<overrides::OverrideStore>>,
    runtime_files: Arc<Mutex<runtime_files::RuntimeFileStore>>,
    user_data_dir: PathBuf,
    service_path: PathBuf,
    service_update_path: PathBuf,
    install_data_dir: PathBuf,
    mihomo_path: PathBuf,
    data_core_dir: PathBuf,
    mihomo_endpoint: String,
    bootstrap_yaml_path: PathBuf,
    app_version: String,
    maintenance: Mutex<()>,
}

struct UiState {
    path: PathBuf,
    child: Option<Child>,
    registered_pid: Option<u32>,
    pending_activation: bool,
}

impl AgentHandler {
    async fn switch_tun(&self, enabled: bool) -> std::result::Result<(), ErrorBody> {
        if enabled {
            let source = self
                .state
                .lock()
                .await
                .runtime_yaml_path
                .clone()
                .unwrap_or_else(|| self.bootstrap_yaml_path.clone());
            let active = self
                .user_data_dir
                .join("runtime")
                .join("_agent_service_active.yaml");
            write_tun_config(&source, &active, true).map_err(internal_error)?;
            self.core.stop_core().await.map_err(internal_error)?;
            if let Err(error) = start_service_core(
                &self.service_path,
                &self.mihomo_path,
                &self.data_core_dir,
                &active,
            ) {
                let _ = self.core.start_core().await;
                return Err(internal_error(error));
            }
        } else {
            run_service_command(&self.service_path, "stop-core", None).map_err(internal_error)?;
            self.core.start_core().await.map_err(internal_error)?;
        }
        self.settings
            .lock()
            .await
            .update_bool("IsTunEnabled", enabled)
            .map_err(internal_error)?;
        Ok(())
    }

    async fn update_state(
        &self,
        desired_running: bool,
        runtime_yaml_path: Option<PathBuf>,
    ) -> std::result::Result<(), ErrorBody> {
        let mut state = self.state.lock().await;
        state.desired_running = desired_running;
        if runtime_yaml_path.is_some() {
            state.runtime_yaml_path = runtime_yaml_path;
        }
        self.state_store.save(&state).map_err(internal_error)
    }

    async fn activate_ui(&self) -> std::result::Result<serde_json::Value, ErrorBody> {
        let mut ui = self.ui.lock().await;
        if ui.registered_pid.is_some() {
            let _ = self.events.send(Outgoing::Event {
                event: "ui.activate_requested".into(),
                data: serde_json::json!({}),
            });
            return Ok(serde_json::json!({ "mode": "activate" }));
        }

        if let Some(child) = ui.child.as_mut() {
            match child.try_wait() {
                Ok(None) => {
                    let pid = child.id();
                    ui.pending_activation = true;
                    return Ok(serde_json::json!({ "mode": "starting", "pid": pid }));
                }
                Ok(Some(_)) => ui.child = None,
                Err(error) => {
                    tracing::warn!("failed to inspect UI process: {error}");
                    ui.child = None;
                }
            }
        }

        let child = Command::new(&ui.path)
            .arg("--agent-ui")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|error| ErrorBody {
                code: "ui.spawn_failed".into(),
                message: error.to_string(),
            })?;
        let pid = child.id();
        ui.child = Some(child);
        ui.pending_activation = true;
        Ok(serde_json::json!({ "mode": "spawn", "pid": pid }))
    }
}

#[async_trait::async_trait]
impl MethodHandler for AgentHandler {
    async fn handle(
        &self,
        method: &str,
        params: serde_json::Value,
    ) -> std::result::Result<serde_json::Value, ErrorBody> {
        let tun_enabled = self
            .settings
            .lock()
            .await
            .load()
            .unwrap_or_default()
            .is_tun_enabled;
        match method {
            "agent.hello" => Ok(serde_json::json!({
                "protocol_version": PROTOCOL_VERSION,
                "pid": std::process::id(),
                "capabilities": [
                    "settings.get",
                    "settings.patch",
                    "settings.events",
                    "subscriptions.v1",
                    "proxy-selections.v1",
                    "overrides.v1",
                    "runtime-files.v1",
                    "sources.v1",
                    "updates.v1",
                    "backups.v1",
                    "service-manager.v1",
                ],
            })),
            "agent.status" => Ok(serde_json::json!({
                "system_proxy_enabled": self.state.lock().await.system_proxy_enabled,
                "tun_enabled": tun_enabled,
            })),
            "settings.get" => {
                let (revision, settings) = self
                    .settings
                    .lock()
                    .await
                    .snapshot()
                    .map_err(internal_error)?;
                Ok(serde_json::json!({
                    "revision": revision,
                    "settings": settings,
                }))
            }
            "settings.patch" => {
                let changes = params
                    .get("changes")
                    .and_then(serde_json::Value::as_object)
                    .ok_or_else(|| ErrorBody {
                        code: "settings.invalid_patch".into(),
                        message: "params is missing changes object".into(),
                    })?;
                let expected_revision = params
                    .get("expected_revision")
                    .and_then(serde_json::Value::as_u64);
                let mut store = self.settings.lock().await;
                store.refresh().map_err(internal_error)?;
                if expected_revision.is_some_and(|expected| expected != store.revision()) {
                    return Err(ErrorBody {
                        code: "settings.revision_conflict".into(),
                        message: format!(
                            "settings revision changed from {} to {}",
                            expected_revision.unwrap_or_default(),
                            store.revision()
                        ),
                    });
                }
                let (revision, _, settings) =
                    store.apply_patch(changes).map_err(internal_error)?;
                drop(store);
                let data = serde_json::json!({
                    "revision": revision,
                    "settings": settings,
                });
                let _ = self.events.send(Outgoing::Event {
                    event: "settings.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "subscriptions.list" => {
                let subscriptions = self
                    .subscriptions
                    .lock()
                    .await
                    .list()
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "subscriptions": subscriptions }))
            }
            "subscriptions.save" => {
                let subscription = params
                    .get("subscription")
                    .cloned()
                    .filter(serde_json::Value::is_object)
                    .ok_or_else(|| ErrorBody {
                        code: "subscriptions.invalid_subscription".into(),
                        message: "params is missing subscription object".into(),
                    })?;
                let subscription_id = subscription
                    .get("Id")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
                    .to_owned();
                let content = params
                    .get("content")
                    .and_then(serde_json::Value::as_str)
                    .ok_or_else(|| ErrorBody {
                        code: "subscriptions.invalid_content".into(),
                        message: "params is missing subscription content".into(),
                    })?;
                self.subscriptions
                    .lock()
                    .await
                    .save(subscription, content)
                    .map_err(internal_error)?;
                let data = serde_json::json!({
                    "kind": "saved",
                    "subscription_id": subscription_id,
                });
                let _ = self.events.send(Outgoing::Event {
                    event: "subscriptions.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "subscriptions.update" => {
                let subscription = params
                    .get("subscription")
                    .cloned()
                    .filter(serde_json::Value::is_object)
                    .ok_or_else(|| ErrorBody {
                        code: "subscriptions.invalid_subscription".into(),
                        message: "params is missing subscription object".into(),
                    })?;
                let subscription_id = subscription
                    .get("Id")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
                    .to_owned();
                self.subscriptions
                    .lock()
                    .await
                    .update(subscription)
                    .map_err(internal_error)?;
                let data = serde_json::json!({
                    "kind": "updated",
                    "subscription_id": subscription_id,
                });
                let _ = self.events.send(Outgoing::Event {
                    event: "subscriptions.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "subscriptions.reorder" => {
                let subscriptions = params
                    .get("subscriptions")
                    .and_then(serde_json::Value::as_array)
                    .ok_or_else(|| ErrorBody {
                        code: "subscriptions.invalid_list".into(),
                        message: "params is missing subscriptions array".into(),
                    })?;
                self.subscriptions
                    .lock()
                    .await
                    .reorder(subscriptions)
                    .map_err(internal_error)?;
                let data = serde_json::json!({ "kind": "reordered" });
                let _ = self.events.send(Outgoing::Event {
                    event: "subscriptions.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "subscriptions.delete" => {
                let subscription_id = required_string_param(
                    &params,
                    "subscription_id",
                    "subscriptions.invalid_id",
                )?;
                self.subscriptions
                    .lock()
                    .await
                    .delete(subscription_id)
                    .map_err(internal_error)?;
                let data = serde_json::json!({
                    "kind": "deleted",
                    "subscription_id": subscription_id,
                });
                let _ = self.events.send(Outgoing::Event {
                    event: "subscriptions.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "subscriptions.content.get" => {
                let subscription_id = required_string_param(
                    &params,
                    "subscription_id",
                    "subscriptions.invalid_id",
                )?;
                let content = self
                    .subscriptions
                    .lock()
                    .await
                    .read_content(subscription_id)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "content": content }))
            }
            "subscriptions.content.set" => {
                let subscription_id = required_string_param(
                    &params,
                    "subscription_id",
                    "subscriptions.invalid_id",
                )?;
                let content = params
                    .get("content")
                    .and_then(serde_json::Value::as_str)
                    .ok_or_else(|| ErrorBody {
                        code: "subscriptions.invalid_content".into(),
                        message: "params is missing subscription content".into(),
                    })?;
                self.subscriptions
                    .lock()
                    .await
                    .save_content(subscription_id, content)
                    .map_err(internal_error)?;
                let data = serde_json::json!({
                    "kind": "content",
                    "subscription_id": subscription_id,
                });
                let _ = self.events.send(Outgoing::Event {
                    event: "subscriptions.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "subscriptions.content.path" => {
                let subscription_id = required_string_param(
                    &params,
                    "subscription_id",
                    "subscriptions.invalid_id",
                )?;
                let path = self
                    .subscriptions
                    .lock()
                    .await
                    .content_path(subscription_id)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "path": path }))
            }
            "subscriptions.selection.get" => {
                let subscription_id = self
                    .subscriptions
                    .lock()
                    .await
                    .current_subscription_id()
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "subscription_id": subscription_id }))
            }
            "subscriptions.selection.set" => {
                let subscription_id = params
                    .get("subscription_id")
                    .filter(|value| !value.is_null())
                    .map(|value| {
                        value.as_str().ok_or_else(|| ErrorBody {
                            code: "subscriptions.invalid_id".into(),
                            message: "subscription_id must be a string or null".into(),
                        })
                    })
                    .transpose()?;
                self.subscriptions
                    .lock()
                    .await
                    .set_current_subscription_id(subscription_id)
                    .map_err(internal_error)?;
                let data = serde_json::json!({ "subscription_id": subscription_id });
                let _ = self.events.send(Outgoing::Event {
                    event: "subscription_selection.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "proxies.selections.get" => {
                let subscription_id = required_string_param(
                    &params,
                    "subscription_id",
                    "proxies.invalid_subscription_id",
                )?;
                let selections = self
                    .subscriptions
                    .lock()
                    .await
                    .proxy_selections(subscription_id)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "selections": selections }))
            }
            "proxies.selections.set" => {
                let subscription_id = required_string_param(
                    &params,
                    "subscription_id",
                    "proxies.invalid_subscription_id",
                )?;
                let group_name =
                    required_string_param(&params, "group_name", "proxies.invalid_group")?;
                let proxy_name =
                    required_string_param(&params, "proxy_name", "proxies.invalid_proxy")?;
                self.subscriptions
                    .lock()
                    .await
                    .set_proxy_selection(subscription_id, group_name, proxy_name)
                    .map_err(internal_error)?;
                let data = serde_json::json!({
                    "kind": "set",
                    "subscription_id": subscription_id,
                    "group_name": group_name,
                    "proxy_name": proxy_name,
                });
                let _ = self.events.send(Outgoing::Event {
                    event: "proxy_selections.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "proxies.selections.remove" => {
                let subscription_id = required_string_param(
                    &params,
                    "subscription_id",
                    "proxies.invalid_subscription_id",
                )?;
                let group_name =
                    required_string_param(&params, "group_name", "proxies.invalid_group")?;
                self.subscriptions
                    .lock()
                    .await
                    .remove_proxy_selection(subscription_id, group_name)
                    .map_err(internal_error)?;
                let data = serde_json::json!({
                    "kind": "removed",
                    "subscription_id": subscription_id,
                    "group_name": group_name,
                });
                let _ = self.events.send(Outgoing::Event {
                    event: "proxy_selections.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "overrides.list" => {
                let profiles = self.overrides.lock().await.list().map_err(internal_error)?;
                Ok(serde_json::json!({ "overrides": profiles }))
            }
            "overrides.save" => {
                let profile = params
                    .get("profile")
                    .cloned()
                    .filter(serde_json::Value::is_object)
                    .ok_or_else(|| ErrorBody {
                        code: "overrides.invalid_profile".into(),
                        message: "params is missing profile object".into(),
                    })?;
                let id = profile
                    .get("Id")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default()
                    .to_owned();
                let content = params
                    .get("content")
                    .and_then(serde_json::Value::as_str)
                    .ok_or_else(|| ErrorBody {
                        code: "overrides.invalid_content".into(),
                        message: "params is missing override content".into(),
                    })?;
                self.overrides
                    .lock()
                    .await
                    .save(profile, content)
                    .map_err(internal_error)?;
                let data = serde_json::json!({ "kind": "saved", "override_id": id });
                let _ = self.events.send(Outgoing::Event {
                    event: "overrides.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "overrides.reorder" => {
                let profiles = params
                    .get("overrides")
                    .and_then(serde_json::Value::as_array)
                    .ok_or_else(|| ErrorBody {
                        code: "overrides.invalid_list".into(),
                        message: "params is missing overrides array".into(),
                    })?;
                self.overrides
                    .lock()
                    .await
                    .reorder(profiles)
                    .map_err(internal_error)?;
                let data = serde_json::json!({ "kind": "reordered" });
                let _ = self.events.send(Outgoing::Event {
                    event: "overrides.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "overrides.content.get" => {
                let id =
                    required_string_param(&params, "override_id", "overrides.invalid_id")?;
                let content = self
                    .overrides
                    .lock()
                    .await
                    .read_content(id)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "content": content }))
            }
            "overrides.content.path" => {
                let id =
                    required_string_param(&params, "override_id", "overrides.invalid_id")?;
                let path = self
                    .overrides
                    .lock()
                    .await
                    .content_path(id)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "path": path }))
            }
            "overrides.delete" => {
                let id =
                    required_string_param(&params, "override_id", "overrides.invalid_id")?;
                self.overrides
                    .lock()
                    .await
                    .delete(id)
                    .map_err(internal_error)?;
                let data = serde_json::json!({ "kind": "deleted", "override_id": id });
                let _ = self.events.send(Outgoing::Event {
                    event: "overrides.changed".into(),
                    data: data.clone(),
                });
                Ok(data)
            }
            "runtime.save" => {
                let subscription_id =
                    required_string_param(&params, "subscription_id", "runtime.invalid_id")?;
                let original = params
                    .get("original")
                    .and_then(serde_json::Value::as_str)
                    .ok_or_else(|| ErrorBody {
                        code: "runtime.invalid_original".into(),
                        message: "params is missing original content".into(),
                    })?;
                let runtime = params
                    .get("runtime")
                    .and_then(serde_json::Value::as_str)
                    .ok_or_else(|| ErrorBody {
                        code: "runtime.invalid_config".into(),
                        message: "params is missing runtime content".into(),
                    })?;
                let (original_path, runtime_path) = self
                    .runtime_files
                    .lock()
                    .await
                    .save(subscription_id, original, runtime)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({
                    "original_path": original_path,
                    "runtime_path": runtime_path,
                }))
            }
            "runtime.save_empty" => {
                let runtime = params
                    .get("runtime")
                    .and_then(serde_json::Value::as_str)
                    .ok_or_else(|| ErrorBody {
                        code: "runtime.invalid_config".into(),
                        message: "params is missing runtime content".into(),
                    })?;
                let path = self
                    .runtime_files
                    .lock()
                    .await
                    .save_empty(runtime)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "runtime_path": path }))
            }
            "runtime.get" => {
                let subscription_id =
                    required_string_param(&params, "subscription_id", "runtime.invalid_id")?;
                let content = self
                    .runtime_files
                    .lock()
                    .await
                    .read(subscription_id)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "content": content }))
            }
            "runtime.delete" => {
                let subscription_id =
                    required_string_param(&params, "subscription_id", "runtime.invalid_id")?;
                self.runtime_files
                    .lock()
                    .await
                    .delete(subscription_id)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({}))
            }
            "runtime.provider.get" => {
                let path =
                    required_string_param(&params, "path", "runtime.invalid_provider_path")?;
                let content = self
                    .runtime_files
                    .lock()
                    .await
                    .read_provider(path)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "content": content }))
            }
            "sources.download" => {
                let source_location =
                    required_string_param(&params, "source_location", "sources.invalid_url")?;
                let user_agent = params
                    .get("user_agent")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or_default();
                let proxy_mode = params
                    .get("proxy_mode")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or("Direct");
                let settings = self.settings.lock().await.load().map_err(internal_error)?;
                sources::download(source_location, user_agent, proxy_mode, &settings)
                    .await
                    .map_err(internal_error)
            }
            "sources.local.get" => {
                let path = required_string_param(&params, "path", "sources.invalid_path")?;
                let content = sources::read_local(path).map_err(internal_error)?;
                Ok(serde_json::json!({ "content": content }))
            }
            "sources.provider.upload" => {
                let vehicle_type = required_string_param(
                    &params,
                    "vehicle_type",
                    "sources.invalid_vehicle_type",
                )?;
                let provider_path = required_string_param(
                    &params,
                    "provider_path",
                    "sources.invalid_provider_path",
                )?;
                let source_path =
                    required_string_param(&params, "source_path", "sources.invalid_source_path")?;
                sources::upload_provider(
                    &self.data_core_dir,
                    vehicle_type,
                    provider_path,
                    source_path,
                )
                .map_err(internal_error)
            }
            "app.update.check" => {
                let channel = params
                    .get("channel")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or("stable");
                updates::check_app_update(
                    &self.app_version,
                    channel,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await
                .map_err(internal_error)
            }
            "core.update" => {
                let _maintenance = self.maintenance.lock().await;
                let current = updates::download_latest_core(
                    &self.mihomo_path,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await;
                let (version, updated) = match current {
                    Ok(result) => result,
                    Err(error) => {
                        return Ok(serde_json::json!({
                            "status": "Failed",
                            "version": null,
                            "message": format!("Core update failed: {error:#}"),
                        }));
                    }
                };
                if !updated {
                    return Ok(serde_json::json!({
                        "status": "UpToDate",
                        "version": version,
                        "message": format!("Core is already up to date at v{version}"),
                    }));
                }
                let restart = if tun_enabled {
                    run_service_command(&self.service_path, "restart-core", None)
                        .map(|_| serde_json::json!({}))
                        .map_err(internal_error)
                } else {
                    self.core
                        .handle("core.restart", serde_json::json!({}))
                        .await
                };
                if let Err(error) = restart {
                    let _ = updates::restore_core(&self.mihomo_path);
                    if tun_enabled {
                        let _ = run_service_command(&self.service_path, "restart-core", None);
                    } else {
                        let _ = self
                            .core
                            .handle("core.restart", serde_json::json!({}))
                            .await;
                    }
                    return Ok(serde_json::json!({
                        "status": "Failed",
                        "version": null,
                        "message": format!("Core update failed: {}", error.message),
                    }));
                }
                let mut running = false;
                for _ in 0..30 {
                    tokio::time::sleep(Duration::from_millis(400)).await;
                    let snapshot = if tun_enabled {
                        service_core_snapshot(&self.service_path, &self.mihomo_endpoint)
                            .unwrap_or_else(|_| serde_json::json!({ "state": "unavailable" }))
                    } else {
                        self.core.status().await
                    };
                    match snapshot.get("state").and_then(serde_json::Value::as_str) {
                        Some("running") => {
                            running = true;
                            break;
                        }
                        Some("crashed" | "stopped") => break,
                        _ => {}
                    }
                }
                if !running {
                    let _ = updates::restore_core(&self.mihomo_path);
                    if tun_enabled {
                        let _ = run_service_command(&self.service_path, "restart-core", None);
                    } else {
                        let _ = self
                            .core
                            .handle("core.restart", serde_json::json!({}))
                            .await;
                    }
                    return Ok(serde_json::json!({
                        "status": "Failed",
                        "version": null,
                        "message": "Core update failed: new core failed to start",
                    }));
                }
                let _ = updates::delete_core_backup(&self.mihomo_path);
                Ok(serde_json::json!({
                    "status": "Updated",
                    "version": version,
                    "message": format!("Core updated to v{version}"),
                }))
            }
            "backups.create" => {
                let _maintenance = self.maintenance.lock().await;
                let destination = params
                    .get("path")
                    .and_then(serde_json::Value::as_str)
                    .filter(|value| !value.trim().is_empty());
                let path = backups::create(&self.user_data_dir, destination)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({
                    "success": true,
                    "message": "Backup created",
                    "path": path,
                }))
            }
            "backups.restore" => {
                let _maintenance = self.maintenance.lock().await;
                let source = params
                    .get("path")
                    .and_then(serde_json::Value::as_str)
                    .filter(|value| !value.trim().is_empty());
                let merge = params
                    .get("mode")
                    .and_then(serde_json::Value::as_str)
                    == Some("Merge");
                let message = backups::restore(&self.user_data_dir, source, merge)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "success": true, "message": message }))
            }
            "backups.webdav.test" => {
                let settings = parse_webdav_settings(&params)?;
                backups::webdav_test(
                    &settings,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await
                .map_err(internal_error)?;
                Ok(serde_json::json!({
                    "success": true,
                    "message": "WebDAV connection succeeded",
                }))
            }
            "backups.webdav.list" => {
                let settings = parse_webdav_settings(&params)?;
                let entries = backups::webdav_list(
                    &settings,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await
                .map_err(internal_error)?;
                Ok(serde_json::json!({ "entries": entries }))
            }
            "backups.webdav.create" => {
                let _maintenance = self.maintenance.lock().await;
                let settings = parse_webdav_settings(&params)?;
                backups::webdav_create(
                    &self.user_data_dir,
                    &settings,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await
                .map_err(internal_error)?;
                Ok(serde_json::json!({
                    "success": true,
                    "message": "WebDAV backup completed",
                }))
            }
            "backups.webdav.restore" => {
                let _maintenance = self.maintenance.lock().await;
                let settings = parse_webdav_settings(&params)?;
                let file_name = params
                    .get("file_name")
                    .and_then(serde_json::Value::as_str)
                    .filter(|value| !value.trim().is_empty());
                let merge = params
                    .get("mode")
                    .and_then(serde_json::Value::as_str)
                    == Some("Merge");
                let message = backups::webdav_restore(
                    &self.user_data_dir,
                    &settings,
                    file_name,
                    merge,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await
                .map_err(internal_error)?;
                Ok(serde_json::json!({ "success": true, "message": message }))
            }
            "backups.webdav.delete" => {
                let _maintenance = self.maintenance.lock().await;
                let settings = parse_webdav_settings(&params)?;
                let file_name = required_string_param(
                    &params,
                    "file_name",
                    "backups.invalid_file_name",
                )?;
                backups::webdav_delete(
                    &settings,
                    file_name,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await
                .map_err(internal_error)?;
                Ok(serde_json::json!({
                    "success": true,
                    "message": "WebDAV backup deleted",
                }))
            }
            "service.status" => Ok(service_manager::status(&self.service_path)),
            "service.install" => {
                let _maintenance = self.maintenance.lock().await;
                let message = service_manager::install(
                    &self.service_update_path,
                    &self.service_path,
                    &self.install_data_dir,
                )
                .map_err(internal_error)?;
                Ok(service_operation_result(if message.is_empty() {
                    "Service mode is installed and running.".into()
                } else {
                    message
                }))
            }
            "service.uninstall" => {
                let _maintenance = self.maintenance.lock().await;
                let message = service_manager::uninstall(
                    &self.service_path,
                    &self.service_update_path,
                )
                .map_err(internal_error)?;
                Ok(service_operation_result(message))
            }
            "service.start_core" => {
                let mihomo_path =
                    required_string_param(&params, "mihomo_path", "service.invalid_mihomo_path")?;
                let data_core_dir = required_string_param(
                    &params,
                    "data_core_dir",
                    "service.invalid_data_core_dir",
                )?;
                let config_path =
                    required_string_param(&params, "config_path", "service.invalid_config_path")?;
                let output = start_service_core(
                    &self.service_path,
                    Path::new(mihomo_path),
                    Path::new(data_core_dir),
                    Path::new(config_path),
                )
                .map_err(internal_error)?;
                Ok(service_operation_result(output))
            }
            "service.stop_core" => {
                let output = run_service_command(&self.service_path, "stop-core", None)
                    .map_err(internal_error)?;
                Ok(service_operation_result(output))
            }
            "service.restart_core" => {
                let output = run_service_command(&self.service_path, "restart-core", None)
                    .map_err(internal_error)?;
                Ok(service_operation_result(output))
            }
            "service.heartbeat" => {
                let output = run_service_command(&self.service_path, "heartbeat", None)
                    .map_err(internal_error)?;
                Ok(service_operation_result(output))
            }
            "agent.shutdown" => {
                self.shutdown_tx.send(()).await.map_err(internal_error)?;
                Ok(serde_json::json!({}))
            }
            "agent.exit_application" => {
                let system_proxy_enabled = self.state.lock().await.system_proxy_enabled;
                if system_proxy_enabled {
                    let settings = self.settings.lock().await.load().unwrap_or_default();
                    if let Err(error) =
                        system_proxy::set_enabled(&self.user_data_dir, &settings, false)
                    {
                        tracing::warn!("failed to disable system proxy during exit: {error:#}");
                    } else {
                        let mut state = self.state.lock().await;
                        state.system_proxy_enabled = false;
                        let _ = self.state_store.save(&state);
                    }
                }
                let _ = self.events.send(Outgoing::Event {
                    event: "ui.shutdown_requested".into(),
                    data: serde_json::json!({}),
                });
                self.shutdown_tx.send(()).await.map_err(internal_error)?;
                Ok(serde_json::json!({}))
            }
            "agent.set_tun" => {
                let enabled = params
                    .get("enabled")
                    .and_then(serde_json::Value::as_bool)
                    .ok_or_else(|| ErrorBody {
                        code: "agent.invalid_tun_state".into(),
                        message: "params is missing enabled".into(),
                    })?;
                self.switch_tun(enabled).await?;
                Ok(serde_json::json!({}))
            }
            "agent.set_system_proxy" => {
                let enabled = params
                    .get("enabled")
                    .and_then(serde_json::Value::as_bool)
                    .ok_or_else(|| ErrorBody {
                        code: "agent.invalid_system_proxy_state".into(),
                        message: "params is missing enabled".into(),
                    })?;
                let mut settings = self.settings.lock().await.load().unwrap_or_default();
                if enabled {
                    if let Some(host) = params.get("host").and_then(serde_json::Value::as_str) {
                        settings.proxy_host = host.to_owned();
                    }
                    if let Some(port) = params
                        .get("port")
                        .and_then(serde_json::Value::as_u64)
                        .and_then(|value| u16::try_from(value).ok())
                    {
                        settings.mixed_port = i32::from(port);
                    }
                    settings.is_pac_mode_enabled = params
                        .get("is_pac_mode_enabled")
                        .and_then(serde_json::Value::as_bool)
                        .unwrap_or(false);
                    settings.pac_script = params
                        .get("pac_script")
                        .and_then(serde_json::Value::as_str)
                        .unwrap_or_default()
                        .to_owned();
                    settings.system_proxy_bypass = params
                        .get("bypass_rules")
                        .and_then(serde_json::Value::as_array)
                        .map(|items| {
                            items
                                .iter()
                                .filter_map(serde_json::Value::as_str)
                                .collect::<Vec<_>>()
                                .join(",")
                        })
                        .unwrap_or_default();
                }
                system_proxy::set_enabled(&self.user_data_dir, &settings, enabled)
                    .map_err(internal_error)?;
                let mut state = self.state.lock().await;
                state.system_proxy_enabled = enabled;
                self.state_store.save(&state).map_err(internal_error)?;
                Ok(serde_json::json!({ "enabled": enabled }))
            }
            "ui.activate" => self.activate_ui().await,
            "ui.register" => {
                let pid = params
                    .get("pid")
                    .and_then(serde_json::Value::as_u64)
                    .and_then(|value| u32::try_from(value).ok())
                    .ok_or_else(|| ErrorBody {
                        code: "ui.invalid_registration".into(),
                        message: "params is missing a valid pid".into(),
                    })?;
                let should_activate = {
                    let mut ui = self.ui.lock().await;
                    ui.registered_pid = Some(pid);
                    std::mem::take(&mut ui.pending_activation)
                };
                if should_activate {
                    let _ = self.events.send(Outgoing::Event {
                        event: "ui.activate_requested".into(),
                        data: serde_json::json!({}),
                    });
                }
                let update_version = std::fs::read_to_string(
                    self.user_data_dir.join("runtime").join("agent-update.json"),
                )
                .ok()
                .and_then(|content| serde_json::from_str::<serde_json::Value>(&content).ok())
                .and_then(|document| {
                    document
                        .get("version")
                        .and_then(serde_json::Value::as_str)
                        .map(str::to_owned)
                });
                Ok(serde_json::json!({
                    "activate": should_activate,
                    "update_version": update_version,
                }))
            }
            "ui.unregister" => {
                let pid = params
                    .get("pid")
                    .and_then(serde_json::Value::as_u64)
                    .and_then(|value| u32::try_from(value).ok());
                let mut ui = self.ui.lock().await;
                if pid.is_none() || ui.registered_pid == pid {
                    ui.registered_pid = None;
                }
                Ok(serde_json::json!({}))
            }
            "core.status" if tun_enabled => {
                service_core_snapshot(&self.service_path, &self.mihomo_endpoint)
                    .map_err(internal_error)
            }
            "core.start" if tun_enabled => {
                if let Ok(snapshot) =
                    service_core_snapshot(&self.service_path, &self.mihomo_endpoint)
                    && snapshot.get("state").and_then(serde_json::Value::as_str) == Some("running")
                {
                    return Ok(serde_json::json!({
                        "pid": snapshot.get("pid").cloned().unwrap_or(serde_json::Value::Null)
                    }));
                }
                let source = self
                    .state
                    .lock()
                    .await
                    .runtime_yaml_path
                    .clone()
                    .ok_or_else(|| ErrorBody {
                        code: "core.config_missing".into(),
                        message: "the service core has no runtime configuration".into(),
                    })?;
                let active = self
                    .user_data_dir
                    .join("runtime")
                    .join("_agent_service_active.yaml");
                write_tun_config(&source, &active, true).map_err(internal_error)?;
                let output = start_service_core(
                    &self.service_path,
                    &self.mihomo_path,
                    &self.data_core_dir,
                    &active,
                )
                .map_err(internal_error)?;
                Ok(serde_json::json!({ "pid": parse_pid(&output) }))
            }
            "core.stop" if tun_enabled => {
                run_service_command(&self.service_path, "stop-core", None)
                    .map_err(internal_error)?;
                self.update_state(false, None).await?;
                Ok(serde_json::json!({}))
            }
            "core.restart" if tun_enabled => {
                let output = run_service_command(&self.service_path, "restart-core", None)
                    .map_err(internal_error)?;
                self.update_state(true, None).await?;
                Ok(serde_json::json!({ "pid": parse_pid(&output) }))
            }
            "core.apply_config" if tun_enabled => {
                let source = params
                    .get("runtime_yaml_path")
                    .and_then(serde_json::Value::as_str)
                    .map(PathBuf::from)
                    .ok_or_else(|| ErrorBody {
                        code: "core.config_missing".into(),
                        message: "params is missing runtime_yaml_path".into(),
                    })?;
                let active = self
                    .user_data_dir
                    .join("runtime")
                    .join("_agent_service_active.yaml");
                write_tun_config(&source, &active, true).map_err(internal_error)?;
                let output = start_service_core(
                    &self.service_path,
                    &self.mihomo_path,
                    &self.data_core_dir,
                    &active,
                )
                .map_err(internal_error)?;
                self.update_state(true, Some(source)).await?;
                Ok(serde_json::json!({ "mode": "restart", "pid": parse_pid(&output) }))
            }
            "core.start" => {
                let result = self.core.handle(method, params).await?;
                self.update_state(true, None).await?;
                Ok(result)
            }
            "core.stop" => {
                let result = self.core.handle(method, params).await?;
                self.update_state(false, None).await?;
                Ok(result)
            }
            "core.restart" => {
                let result = self.core.handle(method, params).await?;
                self.update_state(true, None).await?;
                Ok(result)
            }
            "core.apply_config" => {
                let runtime_yaml_path = params
                    .get("runtime_yaml_path")
                    .and_then(|value| value.as_str())
                    .map(PathBuf::from);
                let result = self.core.handle(method, params).await?;
                self.update_state(true, runtime_yaml_path).await?;
                Ok(result)
            }
            _ => self.core.handle(method, params).await,
        }
    }
}

fn internal_error(error: impl std::fmt::Display) -> ErrorBody {
    ErrorBody {
        code: "agent.internal".into(),
        message: error.to_string(),
    }
}

fn required_string_param<'a>(
    params: &'a serde_json::Value,
    name: &str,
    code: &str,
) -> std::result::Result<&'a str, ErrorBody> {
    params
        .get(name)
        .and_then(serde_json::Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| ErrorBody {
            code: code.into(),
            message: format!("params is missing {name}"),
        })
}

fn parse_webdav_settings(
    params: &serde_json::Value,
) -> std::result::Result<backups::WebDavSettings, ErrorBody> {
    serde_json::from_value(
        params
            .get("settings")
            .cloned()
            .ok_or_else(|| ErrorBody {
                code: "backups.invalid_settings".into(),
                message: "params is missing settings".into(),
            })?,
    )
    .map_err(|error| ErrorBody {
        code: "backups.invalid_settings".into(),
        message: error.to_string(),
    })
}

fn service_operation_result(message: String) -> serde_json::Value {
    serde_json::json!({
        "type": "Succeeded",
        "message": if message.is_empty() {
            "Service command completed."
        } else {
            &message
        },
    })
}

fn main() -> Result<()> {
    init_tracing();
    let options = parse_options()?;
    tray::run(move |command_rx, desktop_proxy| {
        let runtime = match tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
        {
            Ok(runtime) => runtime,
            Err(error) => {
                tracing::error!("failed to create User Agent runtime: {error}");
                let _ = desktop_proxy.send_event(tray::DesktopEvent::BackendStopped);
                return;
            }
        };
        if let Err(error) = runtime.block_on(run(options, command_rx, desktop_proxy.clone())) {
            tracing::error!("User Agent stopped with an error: {error:#}");
        }
        let _ = desktop_proxy.send_event(tray::DesktopEvent::BackendStopped);
    })
}

async fn run(
    options: AgentOptions,
    mut command_rx: mpsc::UnboundedReceiver<tray::AgentCommand>,
    desktop_proxy: tao::event_loop::EventLoopProxy<tray::DesktopEvent>,
) -> Result<()> {
    let runtime_dir = options.user_data_dir.join("runtime");
    std::fs::create_dir_all(&runtime_dir).context("failed to create runtime directory")?;
    let _instance_lock = acquire_instance_lock(&runtime_dir)?;

    let state_store = Arc::new(AgentStateStore::new(&options.user_data_dir));
    let state = state_store.load();
    let settings_store = Arc::new(Mutex::new(settings::SettingsStore::new(
        &options.user_data_dir,
    )));
    let subscription_store = Arc::new(Mutex::new(subscriptions::SubscriptionStore::new(
        &options.user_data_dir,
    )));
    let override_store = Arc::new(Mutex::new(overrides::OverrideStore::new(
        &options.user_data_dir,
    )));
    let runtime_file_store = Arc::new(Mutex::new(runtime_files::RuntimeFileStore::new(
        &options.user_data_dir,
        &options.data_core_dir,
    )));
    let initial_settings = settings_store.lock().await.load().unwrap_or_default();
    let bootstrap_yaml = load_startup_yaml(&options, &state, &initial_settings)?;
    let paths = HubPaths::new(
        options.user_data_dir.clone(),
        options.mihomo_path.clone(),
        options.data_core_dir.clone(),
    );
    paths
        .ensure_dirs()
        .context("failed to prepare core directories")?;

    let (events_tx, _) = broadcast::channel(128);
    let events_rx = events_tx.subscribe();
    let core = Arc::new(CoreRuntime::new(
        paths,
        options.mihomo_endpoint.clone(),
        bootstrap_yaml,
        events_tx.clone(),
    )?);
    let (agent_shutdown_tx, mut agent_shutdown_rx) = mpsc::channel(1);
    let shared_state = Arc::new(Mutex::new(state.clone()));
    let agent_handler = Arc::new(AgentHandler {
        core: core.clone(),
        state: shared_state.clone(),
        state_store,
        shutdown_tx: agent_shutdown_tx,
        events: events_tx.clone(),
        ui: Mutex::new(UiState {
            path: options.ui_path.clone(),
            child: None,
            registered_pid: None,
            pending_activation: false,
        }),
        settings: settings_store.clone(),
        subscriptions: subscription_store.clone(),
        overrides: override_store,
        runtime_files: runtime_file_store,
        user_data_dir: options.user_data_dir.clone(),
        service_path: options.service_path.clone(),
        service_update_path: options.service_update_path.clone(),
        install_data_dir: options.install_data_dir.clone(),
        mihomo_path: options.mihomo_path.clone(),
        data_core_dir: options.data_core_dir.clone(),
        mihomo_endpoint: options.mihomo_endpoint.clone(),
        bootstrap_yaml_path: options.bootstrap_yaml_path.clone(),
        app_version: options.app_version.clone(),
        maintenance: Mutex::new(()),
    });
    let handler: Arc<dyn MethodHandler> = agent_handler.clone();
    let (server, _, server_shutdown_tx) = IpcServer::with_events(handler, events_tx.clone());
    let endpoint = options.endpoint.clone();
    let mut server_task = tokio::spawn(async move { server.serve(&endpoint).await });
    let supervisor_task = tokio::spawn(supervise_core(
        core.clone(),
        shared_state.clone(),
        events_rx,
    ));
    let service_log_task = tokio::spawn(supervise_service_core_logs(
        options.mihomo_endpoint.clone(),
        settings_store.clone(),
        events_tx.clone(),
    ));
    let service_state_task = tokio::spawn(supervise_service_core_state(
        options.service_path.clone(),
        settings_store.clone(),
        events_tx.clone(),
    ));
    let mut tray_events = events_tx.subscribe();
    let tray_proxy = desktop_proxy.clone();
    let tray_state_task = tokio::spawn(async move {
        while let Ok(Outgoing::Event { event, data }) = tray_events.recv().await {
            if event == "core.state_changed" {
                let core_running =
                    data.get("state").and_then(serde_json::Value::as_str) == Some("running");
                let _ = tray_proxy.send_event(tray::DesktopEvent::CoreRunning(core_running));
            }
        }
    });

    let mut tray_state = tray::TrayState {
        core_running: false,
        system_proxy_enabled: state.system_proxy_enabled,
        tun_enabled: initial_settings.is_tun_enabled,
        outbound_mode: parse_outbound_mode(&initial_settings.outbound_mode),
    };
    let _ = desktop_proxy.send_event(tray::DesktopEvent::StateChanged(tray_state.clone()));
    let _ = desktop_proxy.send_event(tray::DesktopEvent::HotKeysChanged([
        initial_settings.window_toggle_hotkey.clone(),
        initial_settings.system_proxy_toggle_hotkey.clone(),
        initial_settings.tun_toggle_hotkey.clone(),
    ]));
    let _ = desktop_proxy.send_event(tray::DesktopEvent::DoubleClickChanged(
        initial_settings.is_tray_double_click_enabled,
    ));
    let mut settings_poll = tokio::time::interval(Duration::from_secs(2));
    let mut service_heartbeat = tokio::time::interval(Duration::from_secs(30));
    let mut pending_tun_switch = None;
    let mut background_scheduler = background::BackgroundScheduler::new(
        options.user_data_dir.clone(),
        options.app_version.clone(),
        events_tx.clone(),
        agent_handler.clone(),
        subscription_store,
    );
    let background_settings = settings_store.clone();
    let background_task = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_secs(60));
        loop {
            interval.tick().await;
            background_scheduler.tick(background_settings.clone()).await;
        }
    });

    if state.desired_running {
        let startup = if initial_settings.is_tun_enabled {
            switch_tun_host(true, &options, &shared_state, &core).await
        } else {
            core.start_core().await
        };
        if let Err(error) = startup {
            tracing::error!("initial core startup failed: {error:#}");
        } else if initial_settings.is_tun_enabled {
            tray_state.core_running = true;
            let _ = desktop_proxy.send_event(tray::DesktopEvent::StateChanged(tray_state.clone()));
        }
    }

    loop {
        tokio::select! {
            signal = shutdown_signal() => {
                signal.context("failed to listen for agent shutdown signal")?;
                break;
            }
            _ = agent_shutdown_rx.recv() => break,
            _ = settings_poll.tick() => {
                match settings_store.lock().await.reload_if_changed() {
                    Ok(Some(changed)) => {
                        if changed.is_tun_enabled != tray_state.tun_enabled {
                            if let Err(error) = switch_tun_host(
                                changed.is_tun_enabled,
                                &options,
                                &shared_state,
                                &core,
                            ).await {
                                tracing::warn!("failed to reconcile changed TUN setting: {error:#}");
                                pending_tun_switch = Some(changed.is_tun_enabled);
                            } else {
                                tray_state.tun_enabled = changed.is_tun_enabled;
                                tray_state.core_running = true;
                                pending_tun_switch = None;
                            }
                        } else {
                            pending_tun_switch = None;
                        }
                        tray_state.outbound_mode = parse_outbound_mode(&changed.outbound_mode);
                        let _ = desktop_proxy.send_event(
                            tray::DesktopEvent::StateChanged(tray_state.clone())
                        );
                        let _ = desktop_proxy.send_event(tray::DesktopEvent::HotKeysChanged([
                            changed.window_toggle_hotkey,
                            changed.system_proxy_toggle_hotkey,
                            changed.tun_toggle_hotkey,
                        ]));
                        let _ = desktop_proxy.send_event(
                            tray::DesktopEvent::DoubleClickChanged(
                                changed.is_tray_double_click_enabled,
                            )
                        );
                    }
                    Ok(None) => {
                        if let Some(enabled) = pending_tun_switch {
                            if let Err(error) = switch_tun_host(
                                enabled,
                                &options,
                                &shared_state,
                                &core,
                            ).await {
                                tracing::warn!("failed to retry TUN setting reconciliation: {error:#}");
                            } else {
                                tray_state.tun_enabled = enabled;
                                tray_state.core_running = true;
                                pending_tun_switch = None;
                                let _ = desktop_proxy.send_event(
                                    tray::DesktopEvent::StateChanged(tray_state.clone())
                                );
                            }
                        }
                    }
                    Err(error) => tracing::warn!("failed to reload settings: {error:#}"),
                }
            }
            _ = service_heartbeat.tick() => {
                send_service_heartbeat(&options.service_path);
            }
            command = command_rx.recv() => {
                match command {
                    Some(tray::AgentCommand::ActivateUi) => {
                        let _ = agent_handler.activate_ui().await;
                    }
                    Some(tray::AgentCommand::RestartCore) => {
                        if tray_state.tun_enabled {
                            if let Err(error) = run_service_command(
                                &options.service_path,
                                "restart-core",
                                None,
                            ) {
                                tracing::warn!("failed to restart service core: {error:#}");
                            }
                        } else {
                            let _ = core.handle("core.restart", serde_json::json!({})).await;
                        }
                    }
                    Some(tray::AgentCommand::CopyTerminal(shell)) => {
                        let settings = settings_store.lock().await.load().unwrap_or_default();
                        if let Err(error) = copy_terminal_proxy(shell, &settings) {
                            tracing::warn!("failed to copy terminal proxy command: {error:#}");
                        }
                    }
                    Some(tray::AgentCommand::SetOutboundMode(mode)) => {
                        let mode_name = outbound_mode_name(mode);
                        match core.handle(
                            "core.set_outbound_mode",
                            serde_json::json!({ "mode": mode_name.to_ascii_lowercase() }),
                        ).await {
                            Ok(_) => {
                                if let Err(error) = settings_store
                                    .lock().await
                                    .update_string("OutboundMode", mode_name)
                                {
                                    tracing::warn!("failed to persist outbound mode: {error:#}");
                                }
                                tray_state.outbound_mode = mode;
                                let _ = desktop_proxy.send_event(
                                    tray::DesktopEvent::StateChanged(tray_state.clone())
                                );
                            }
                            Err(error) => tracing::warn!("failed to set outbound mode: {}", error.message),
                        }
                    }
                    Some(tray::AgentCommand::ToggleSystemProxy) => {
                        let enabled = !tray_state.system_proxy_enabled;
                        let settings = settings_store.lock().await.load().unwrap_or_default();
                        match system_proxy::set_enabled(&options.user_data_dir, &settings, enabled) {
                            Ok(()) => {
                                tray_state.system_proxy_enabled = enabled;
                                let mut state = shared_state.lock().await;
                                state.system_proxy_enabled = enabled;
                                if let Err(error) = agent_handler.state_store.save(&state) {
                                    tracing::warn!("failed to persist system proxy state: {error:#}");
                                }
                                let _ = desktop_proxy.send_event(
                                    tray::DesktopEvent::StateChanged(tray_state.clone())
                                );
                            }
                            Err(error) => tracing::warn!("failed to toggle system proxy: {error:#}"),
                        }
                    }
                    Some(tray::AgentCommand::ToggleTun) => {
                        let enabled = !tray_state.tun_enabled;
                        match switch_tun_host(
                            enabled,
                            &options,
                            &shared_state,
                            &core,
                        ).await {
                            Ok(()) => {
                                if let Err(error) = settings_store
                                    .lock().await
                                    .update_bool("IsTunEnabled", enabled)
                                {
                                    tracing::warn!("failed to persist TUN state: {error:#}");
                                }
                                tray_state.tun_enabled = enabled;
                                tray_state.core_running = true;
                                let _ = desktop_proxy.send_event(
                                    tray::DesktopEvent::StateChanged(tray_state.clone())
                                );
                                let _ = agent_handler.events.send(Outgoing::Event {
                                    event: "agent.tun_change_requested".into(),
                                    data: serde_json::json!({ "enabled": enabled }),
                                });
                            }
                            Err(error) => tracing::warn!("failed to switch TUN host: {error:#}"),
                        }
                    }
                    Some(tray::AgentCommand::ExitApplication) | None => {
                        let _ = agent_handler.events.send(Outgoing::Event {
                            event: "ui.shutdown_requested".into(),
                            data: serde_json::json!({}),
                        });
                        break;
                    }
                }
            }
            result = &mut server_task => {
                result.context("agent IPC task failed")??;
                bail!("agent IPC server exited unexpectedly");
            }
        }
    }

    let current_settings = settings_store.lock().await.load().unwrap_or_default();
    if shared_state.lock().await.system_proxy_enabled {
        if let Err(error) =
            system_proxy::set_enabled(&options.user_data_dir, &current_settings, false)
        {
            tracing::warn!("failed to disable system proxy during shutdown: {error:#}");
        } else {
            let mut state = shared_state.lock().await;
            state.system_proxy_enabled = false;
            if let Err(error) = agent_handler.state_store.save(&state) {
                tracing::warn!("failed to persist system proxy shutdown state: {error:#}");
            }
        }
    }
    if current_settings.is_tun_enabled
        && let Err(error) = run_service_command(&options.service_path, "stop-core", None)
    {
        tracing::warn!("failed to stop service core during exit: {error:#}");
    }
    let _ = core.stop_core().await;
    supervisor_task.abort();
    service_log_task.abort();
    service_state_task.abort();
    tray_state_task.abort();
    background_task.abort();
    let _ = server_shutdown_tx.send(()).await;
    cleanup_endpoint(&options.endpoint);
    Ok(())
}

async fn shutdown_signal() -> Result<()> {
    #[cfg(unix)]
    {
        let mut terminate =
            tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
        tokio::select! {
            result = tokio::signal::ctrl_c() => result?,
            _ = terminate.recv() => {}
        }
        Ok(())
    }

    #[cfg(not(unix))]
    {
        tokio::signal::ctrl_c().await?;
        Ok(())
    }
}

async fn supervise_core(
    core: Arc<CoreRuntime>,
    state: Arc<Mutex<AgentState>>,
    mut events: broadcast::Receiver<Outgoing>,
) {
    while let Ok(event) = events.recv().await {
        let Outgoing::Event { event, data } = event else {
            continue;
        };
        if event != "core.state_changed"
            || data.get("state").and_then(serde_json::Value::as_str) != Some("crashed")
        {
            continue;
        }

        tokio::time::sleep(Duration::from_secs(2)).await;
        if !state.lock().await.desired_running {
            continue;
        }
        if let Err(error) = core.start_core().await {
            tracing::error!("core recovery failed: {error:#}");
        }
    }
}

async fn supervise_service_core_logs(
    endpoint: String,
    settings: Arc<Mutex<settings::SettingsStore>>,
    events: broadcast::Sender<Outgoing>,
) {
    loop {
        let tun_enabled = settings
            .lock()
            .await
            .load()
            .unwrap_or_default()
            .is_tun_enabled;
        if !tun_enabled {
            tokio::time::sleep(Duration::from_secs(2)).await;
            continue;
        }
        let outgoing = events.clone();
        let result = CoreApiClient::new(&endpoint)
            .stream_logs("info", move |line| {
                let _ = outgoing.send(Outgoing::Event {
                    event: "core_logs.entry".into(),
                    data: serde_json::json!({ "line": line }),
                });
            })
            .await;
        if let Err(error) = result {
            tracing::warn!("service core log stream ended: {error:#}");
        }
        tokio::time::sleep(Duration::from_secs(2)).await;
    }
}

async fn supervise_service_core_state(
    service_path: PathBuf,
    settings: Arc<Mutex<settings::SettingsStore>>,
    events: broadcast::Sender<Outgoing>,
) {
    let mut previous = None;
    loop {
        tokio::time::sleep(Duration::from_secs(2)).await;
        if !settings
            .lock()
            .await
            .load()
            .unwrap_or_default()
            .is_tun_enabled
        {
            previous = None;
            continue;
        }
        let snapshot = service_core_snapshot(&service_path, "");
        let (state, pid, reason) = match snapshot {
            Ok(snapshot) => (
                snapshot
                    .get("state")
                    .and_then(serde_json::Value::as_str)
                    .unwrap_or("unavailable")
                    .to_owned(),
                snapshot.get("pid").and_then(serde_json::Value::as_u64),
                snapshot
                    .get("last_error")
                    .and_then(serde_json::Value::as_str)
                    .map(str::to_owned),
            ),
            Err(error) => ("unavailable".into(), None, Some(format!("{error:#}"))),
        };
        let current = (state, pid, reason);
        if previous.as_ref() == Some(&current) {
            continue;
        }
        previous = Some(current.clone());
        let _ = events.send(Outgoing::Event {
            event: "core.state_changed".into(),
            data: serde_json::json!({
                "state": current.0,
                "pid": current.1,
                "reason": current.2,
            }),
        });
    }
}

fn load_startup_yaml(
    options: &AgentOptions,
    state: &AgentState,
    settings: &settings::AgentSettings,
) -> Result<String> {
    if let Some(runtime_path) = state.runtime_yaml_path.as_deref()
        && let Ok(content) = std::fs::read_to_string(runtime_path)
    {
        return Ok(content);
    }

    let controller_key = if cfg!(windows) {
        "external-controller-pipe"
    } else {
        "external-controller-unix"
    };
    let content = format!(
        "mixed-port: {}\nallow-lan: false\nmode: rule\nlog-level: {}\n{}: {}\nproxies: []\nproxy-groups: []\nrules: []\n",
        settings.mixed_port,
        settings.core_log_level,
        controller_key,
        options.mihomo_endpoint,
    );
    let parent = options
        .bootstrap_yaml_path
        .parent()
        .context("bootstrap yaml path has no parent")?;
    std::fs::create_dir_all(parent).context("failed to create bootstrap yaml directory")?;
    std::fs::write(&options.bootstrap_yaml_path, &content).with_context(|| {
        format!(
            "failed to write bootstrap yaml: {}",
            options.bootstrap_yaml_path.display()
        )
    })?;
    Ok(content)
}

fn acquire_instance_lock(runtime_dir: &Path) -> Result<File> {
    let lock_path = runtime_dir.join("agent.lock");
    let file = OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(&lock_path)
        .with_context(|| format!("failed to open agent lock: {}", lock_path.display()))?;
    file.try_lock_exclusive()
        .map_err(|_| anyhow::anyhow!("another user agent instance is already running"))?;
    Ok(file)
}

fn parse_options() -> Result<AgentOptions> {
    let mut values = HashMap::new();
    let mut arguments = std::env::args().skip(1);
    while let Some(name) = arguments.next() {
        if !name.starts_with("--") {
            bail!("unexpected argument: {name}");
        }
        let value = arguments
            .next()
            .with_context(|| format!("missing value for {name}"))?;
        values.insert(name, value);
    }

    Ok(AgentOptions {
        endpoint: required_value(&mut values, "--endpoint")?,
        mihomo_path: PathBuf::from(required_value(&mut values, "--mihomo-path")?),
        data_core_dir: PathBuf::from(required_value(&mut values, "--data-core-dir")?),
        user_data_dir: PathBuf::from(required_value(&mut values, "--user-data-dir")?),
        mihomo_endpoint: required_value(&mut values, "--mihomo-endpoint")?,
        bootstrap_yaml_path: PathBuf::from(required_value(&mut values, "--bootstrap-yaml-path")?),
        ui_path: PathBuf::from(required_value(&mut values, "--ui-path")?),
        service_path: PathBuf::from(required_value(&mut values, "--service-path")?),
        service_update_path: PathBuf::from(required_value(
            &mut values,
            "--service-update-path",
        )?),
        install_data_dir: PathBuf::from(required_value(&mut values, "--install-data-dir")?),
        app_version: required_value(&mut values, "--app-version")?,
    })
}

fn required_value(values: &mut HashMap<String, String>, name: &str) -> Result<String> {
    values
        .remove(name)
        .with_context(|| format!("missing required option: {name}"))
}

fn init_tracing() {
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info"));
    let _ = tracing_subscriber::fmt().with_env_filter(filter).try_init();
}

fn cleanup_endpoint(endpoint: &str) {
    #[cfg(unix)]
    {
        let _ = std::fs::remove_file(endpoint);
    }
    #[cfg(windows)]
    {
        let _ = endpoint;
    }
}

fn parse_outbound_mode(value: &str) -> tray::OutboundMode {
    match value.to_ascii_lowercase().as_str() {
        "global" => tray::OutboundMode::Global,
        "direct" => tray::OutboundMode::Direct,
        _ => tray::OutboundMode::Rule,
    }
}

fn outbound_mode_name(mode: tray::OutboundMode) -> &'static str {
    match mode {
        tray::OutboundMode::Rule => "Rule",
        tray::OutboundMode::Global => "Global",
        tray::OutboundMode::Direct => "Direct",
    }
}

fn copy_terminal_proxy(
    shell: tray::TerminalShell,
    settings: &settings::AgentSettings,
) -> Result<()> {
    use std::io::Write;

    let url = format!("http://{}:{}", settings.proxy_host, settings.mixed_port);
    let command = match shell {
        tray::TerminalShell::PowerShell => {
            format!("$env:http_proxy=\"{url}\"; $env:https_proxy=\"{url}\"")
        }
        tray::TerminalShell::Cmd => {
            format!("set http_proxy={url} && set https_proxy={url}")
        }
        tray::TerminalShell::Bash => {
            format!("export http_proxy={url} && export https_proxy={url}")
        }
    };
    #[cfg(target_os = "macos")]
    let mut child = Command::new("/usr/bin/pbcopy")
        .stdin(Stdio::piped())
        .spawn()?;
    #[cfg(target_os = "windows")]
    let mut child = Command::new("clip.exe").stdin(Stdio::piped()).spawn()?;
    #[cfg(target_os = "linux")]
    let mut child = Command::new("xclip")
        .args(["-selection", "clipboard"])
        .stdin(Stdio::piped())
        .spawn()?;
    let stdin = child
        .stdin
        .as_mut()
        .context("clipboard process has no stdin")?;
    stdin.write_all(command.as_bytes())?;
    child.wait().context("clipboard process failed")?;
    Ok(())
}

fn send_service_heartbeat(service_path: &Path) {
    if !service_path.is_file() {
        return;
    }
    match Command::new(service_path)
        .arg("heartbeat")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
    {
        Ok(status) if status.success() => {}
        Ok(_) => {}
        Err(error) => tracing::warn!("failed to send service heartbeat: {error}"),
    }
}

async fn switch_tun_host(
    enabled: bool,
    options: &AgentOptions,
    state: &Arc<Mutex<AgentState>>,
    core: &Arc<CoreRuntime>,
) -> Result<()> {
    if enabled {
        if !options.service_path.is_file() {
            bail!(
                "service executable was not found: {}",
                options.service_path.display()
            );
        }
        let source = state
            .lock()
            .await
            .runtime_yaml_path
            .clone()
            .unwrap_or_else(|| options.bootstrap_yaml_path.clone());
        let active = options
            .user_data_dir
            .join("runtime")
            .join("_agent_service_active.yaml");
        write_tun_config(&source, &active, true)?;
        core.stop_core().await?;
        if let Err(error) = start_service_core(
            &options.service_path,
            &options.mihomo_path,
            &options.data_core_dir,
            &active,
        ) {
            let _ = core.start_core().await;
            return Err(error);
        }
        return Ok(());
    }

    run_service_command(&options.service_path, "stop-core", None)?;
    core.start_core().await?;
    Ok(())
}

fn write_tun_config(source: &Path, destination: &Path, enabled: bool) -> Result<()> {
    let content = std::fs::read_to_string(source)
        .with_context(|| format!("failed to read runtime config: {}", source.display()))?;
    let mut yaml: serde_yaml_ng::Value =
        serde_yaml_ng::from_str(&content).context("runtime config is invalid")?;
    let root = yaml
        .as_mapping_mut()
        .context("runtime config root must be a mapping")?;
    let tun_key = serde_yaml_ng::Value::String("tun".into());
    let tun = root
        .entry(tun_key)
        .or_insert_with(|| serde_yaml_ng::Value::Mapping(Default::default()));
    let tun = tun
        .as_mapping_mut()
        .context("runtime tun config must be a mapping")?;
    tun.insert(
        serde_yaml_ng::Value::String("enable".into()),
        serde_yaml_ng::Value::Bool(enabled),
    );
    let parent = destination.parent().context("TUN config has no parent")?;
    std::fs::create_dir_all(parent)?;
    std::fs::write(destination, serde_yaml_ng::to_string(&yaml)?)?;
    Ok(())
}

fn start_service_core(
    service_path: &Path,
    mihomo_path: &Path,
    data_core_dir: &Path,
    config_path: &Path,
) -> Result<String> {
    let payload = serde_json::json!({
        "type": "StartCore",
        "data": {
            "mihomo_path": mihomo_path,
            "config_path": config_path,
            "data_core_dir": data_core_dir,
        }
    })
    .to_string();
    run_service_command(service_path, "start-core", Some(&payload))
}

fn run_service_command(service_path: &Path, command: &str, input: Option<&str>) -> Result<String> {
    use std::io::Write;

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
        .with_context(|| format!("failed to run service command: {command}"))?;
    if let Some(input) = input {
        process
            .stdin
            .as_mut()
            .context("service command has no stdin")?
            .write_all(input.as_bytes())?;
    }
    let output = process.wait_with_output()?;
    if output.status.success() {
        return Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned());
    }
    bail!(
        "service command {command} failed: {}",
        String::from_utf8_lossy(&output.stderr).trim()
    )
}

fn parse_pid(output: &str) -> Option<u32> {
    output.split_whitespace().find_map(|part| {
        part.strip_prefix("pid=")
            .and_then(|value| value.parse().ok())
    })
}

fn service_core_snapshot(service_path: &Path, endpoint: &str) -> Result<serde_json::Value> {
    let output = run_service_command(service_path, "status", None)?;
    let mut state = "unavailable";
    let mut pid = None;
    let mut error = None;
    for part in output.split_whitespace() {
        if let Some(value) = part.strip_prefix("core=") {
            state = value;
        } else if let Some(value) = part.strip_prefix("pid=") {
            pid = value.parse::<u32>().ok();
        } else if let Some(value) = part.strip_prefix("error=") {
            error = Some(value.to_owned());
        }
    }
    Ok(serde_json::json!({
        "state": state,
        "pid": pid,
        "external_controller": endpoint,
        "last_error": error,
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn create(name: &str) -> Result<Self> {
            let path =
                std::env::temp_dir().join(format!("sbproxy-agent-{name}-{}", std::process::id()));
            if path.exists() {
                std::fs::remove_dir_all(&path)?;
            }
            std::fs::create_dir_all(&path)?;
            Ok(Self(path))
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn state_store_round_trips_desired_state_and_runtime_path() -> Result<()> {
        let directory = TestDirectory::create("state")?;
        let store = AgentStateStore::new(&directory.0);
        let expected = AgentState {
            desired_running: false,
            runtime_yaml_path: Some(directory.0.join("runtime.yaml")),
            system_proxy_enabled: true,
        };

        store.save(&expected)?;
        let actual = store.load();

        assert!(!actual.desired_running);
        assert_eq!(actual.runtime_yaml_path, expected.runtime_yaml_path);
        assert!(actual.system_proxy_enabled);
        Ok(())
    }

    #[test]
    fn startup_yaml_prefers_last_applied_runtime_config() -> Result<()> {
        let directory = TestDirectory::create("startup-yaml")?;
        let bootstrap_path = directory.0.join("bootstrap.yaml");
        let runtime_path = directory.0.join("runtime.yaml");
        std::fs::write(&bootstrap_path, "mixed-port: 7890")?;
        std::fs::write(&runtime_path, "mixed-port: 7891")?;
        let options = AgentOptions {
            endpoint: "agent".into(),
            mihomo_path: directory.0.join("mihomo"),
            data_core_dir: directory.0.clone(),
            user_data_dir: directory.0.clone(),
            mihomo_endpoint: "mihomo".into(),
            bootstrap_yaml_path: bootstrap_path,
            ui_path: directory.0.join("ui"),
            service_path: directory.0.join("service"),
            service_update_path: directory.0.join("service-update"),
            install_data_dir: directory.0.clone(),
            app_version: "1.0.0".into(),
        };
        let state = AgentState {
            desired_running: true,
            runtime_yaml_path: Some(runtime_path),
            system_proxy_enabled: false,
        };

        let yaml = load_startup_yaml(&options, &state, &settings::AgentSettings::default())?;

        assert_eq!(yaml, "mixed-port: 7891");
        Ok(())
    }
}
