use std::collections::HashSet;
use std::fs::{File, OpenOptions};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, Result, bail};
use fs2::FileExt;
use hub::infra::core_api::CoreApiClient;
use hub::infra::core_runtime::CoreRuntime;
use hub::infra::ipc::{ConnectionId, ErrorBody, IpcServer, MethodHandler, Outgoing};
use hub::infra::paths::HubPaths;
#[cfg(unix)]
use interprocess::local_socket::{GenericFilePath, ToFsName};
#[cfg(windows)]
use interprocess::local_socket::{GenericNamespaced, ToNsName};
use interprocess::local_socket::{
    tokio::Stream as TokioIpcStream,
    traits::tokio::Stream as TokioStreamExt,
};
use serde::{Deserialize, Serialize};
use tokio::io::{AsyncBufRead, AsyncBufReadExt, AsyncWrite, AsyncWriteExt, BufReader};
use tokio::sync::{Mutex, broadcast, mpsc};

mod background;
mod backups;
mod overrides;
mod runtime_files;
mod settings;
mod service_manager;
mod sources;
mod storage;
mod subscriptions;
mod system_proxy;
mod tray;
mod updates;

/// 用途：定义 Agent 与 UI/其他进程之间 IPC 协议的版本号。
const PROTOCOL_VERSION: u32 = 2;

/// 用途：保存启动 Agent 所需的路径、端点和运行参数。
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
    silent_start: bool,
}

/// 用途：记录 Agent 的持久化运行状态。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
struct AgentState {
    desired_running: bool,
    runtime_yaml_path: Option<PathBuf>,
    system_proxy_enabled: bool,
}

/// 用途：提供持久化 Agent 状态的默认值。
impl Default for AgentState {
    /// 用途：提供 Agent 的默认运行状态。
    fn default() -> Self {
        Self {
            desired_running: true,
            runtime_yaml_path: None,
            system_proxy_enabled: false,
        }
    }
}

/// 用途：负责从磁盘加载和原子保存 Agent 状态。
struct AgentStateStore {
    path: PathBuf,
}

/// 用途：提供 Agent 状态的加载和原子保存接口。
impl AgentStateStore {
    /// 用途：根据用户数据目录创建状态存储。
    fn new(user_data_dir: &Path) -> Self {
        Self {
            path: user_data_dir.join("runtime").join("agent-state.json"),
        }
    }

    /// 用途：读取持久化状态，读取失败时回退到默认值。
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

    /// 用途：将 Agent 状态序列化并安全写入磁盘。
    fn save(&self, state: &AgentState) -> Result<()> {
        let content =
            serde_json::to_vec_pretty(state).context("failed to serialize agent state")?;
        storage::file_write_atomic(&self.path, &content)
    }
}

/// 用途：在 Agent 状态缺少有效运行配置路径时，从当前订阅的既有配置恢复。
fn state_runtime_path_recover(
    user_data_dir: &Path,
    state_store: &AgentStateStore,
    subscription_store: &subscriptions::SubscriptionStore,
    state: &mut AgentState,
) -> Result<bool> {
    if state
        .runtime_yaml_path
        .as_deref()
        .is_some_and(Path::is_file)
    {
        return Ok(false);
    }

    let Some(subscription_id) = subscription_store.current_subscription_id()? else {
        return Ok(false);
    };
    storage::entity_id_validate(&subscription_id, "subscription")?;
    if matches!(subscription_id.as_str(), "." | "..") {
        bail!("subscription id contains invalid path components");
    }

    let runtime_yaml_path = user_data_dir
        .join("runtime")
        .join(&subscription_id)
        .join("runtime.yaml");
    let content = match std::fs::read_to_string(&runtime_yaml_path) {
        Ok(content) => content,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(error) => {
            return Err(error).with_context(|| {
                format!(
                    "failed to read recoverable runtime config: {}",
                    runtime_yaml_path.display()
                )
            });
        }
    };
    let yaml: serde_yaml_ng::Value = serde_yaml_ng::from_str(&content)
        .with_context(|| format!("runtime config is invalid: {}", runtime_yaml_path.display()))?;
    if !yaml.is_mapping() {
        bail!(
            "runtime config root must be a mapping: {}",
            runtime_yaml_path.display()
        );
    }

    state.runtime_yaml_path = Some(runtime_yaml_path.clone());
    state_store.save(state)?;
    tracing::info!(
        "recovered Agent runtime config from selected subscription: {}",
        runtime_yaml_path.display()
    );
    Ok(true)
}

/// 用途：聚合 Agent 的核心运行时、状态、IPC 连接和各类存储。
struct AgentHandler {
    core: Arc<CoreRuntime>,
    state: Arc<Mutex<AgentState>>,
    state_store: Arc<AgentStateStore>,
    shutdown_tx: mpsc::Sender<()>,
    events: broadcast::Sender<Outgoing>,
    active_connections: Mutex<HashSet<ConnectionId>>,
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

/// 用途：维护 UI 子进程及其 IPC 注册信息。
struct UiState {
    path: PathBuf,
    child: Option<Child>,
    registration: Option<UiRegistration>,
    pending_activation: bool,
}

/// 用途：标识已注册 UI 的连接和进程。
#[derive(Debug, Clone, Copy)]
struct UiRegistration {
    connection_id: ConnectionId,
    pid: u32,
}

/// 用途：提供 Agent 本地业务操作及 UI 生命周期协调能力。
impl AgentHandler {
    /// 用途：启用或停用 TUN，并协调核心服务的切换。
    async fn tun_switch(&self, enabled: bool) -> std::result::Result<(), ErrorBody> {
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
        tun_core_switch(
            enabled,
            &self.service_path,
            &self.mihomo_path,
            &self.data_core_dir,
            &source,
            &active,
            &self.core,
        )
        .await
        .map_err(internal_error)?;
        self.settings
            .lock()
            .await
            .update_bool("IsTunEnabled", enabled)
            .map_err(internal_error)?;
        Ok(())
    }

    /// 用途：更新并持久化 Agent 的期望运行状态及配置路径。
    async fn state_update(
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

    /// 用途：请求已注册的 UI 激活自身，必要时返回激活模式。
    async fn ui_activate(&self) -> std::result::Result<serde_json::Value, ErrorBody> {
        let mut ui = self.ui.lock().await;
        if let Some(registration) = ui.registration {
            if ui_child_is_valid(&mut ui, registration.pid) {
                let _ = self.events.send(Outgoing::Event {
                    event: "ui.activate_requested".into(),
                    data: serde_json::json!({}),
                });
                return Ok(serde_json::json!({ "mode": "activate" }));
            }
            ui.registration = None;
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

    /// 用途：校验 UI 子进程并登记其 IPC 连接。
    async fn ui_register(
        &self,
        connection_id: ConnectionId,
        params: &serde_json::Value,
    ) -> std::result::Result<serde_json::Value, ErrorBody> {
        let pid = params
            .get("pid")
            .and_then(serde_json::Value::as_u64)
            .and_then(|value| u32::try_from(value).ok())
            .ok_or_else(|| ErrorBody {
                code: "ui.invalid_registration".into(),
                message: "params is missing a valid pid".into(),
            })?;
        let should_activate = {
            let active_connections = self.active_connections.lock().await;
            if !active_connections.contains(&connection_id) {
                return Err(ErrorBody {
                    code: "ui.connection_closed".into(),
                    message: "UI registration connection is already closed".into(),
                });
            }
            let mut ui = self.ui.lock().await;
            if !ui_child_is_valid(&mut ui, pid) {
                return Err(ErrorBody {
                    code: "ui.invalid_registration".into(),
                    message: "UI process was not started by this User Agent".into(),
                });
            }
            ui.registration = Some(UiRegistration { connection_id, pid });
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

    /// 用途：移除指定连接对应的 UI 注册信息。
    async fn ui_unregister(
        &self,
        connection_id: ConnectionId,
        params: &serde_json::Value,
    ) -> serde_json::Value {
        let pid = params
            .get("pid")
            .and_then(serde_json::Value::as_u64)
            .and_then(|value| u32::try_from(value).ok());
        let mut ui = self.ui.lock().await;
        if ui.registration.is_some_and(|registration| {
            registration.connection_id == connection_id && Some(registration.pid) == pid
        }) {
            ui.registration = None;
        }
        serde_json::json!({})
    }

    /// 用途：关闭并清理已断开的 UI 连接。
    async fn ui_connection_close(&self, connection_id: ConnectionId) {
        let mut ui = self.ui.lock().await;
        if !ui
            .registration
            .is_some_and(|registration| registration.connection_id == connection_id)
        {
            return;
        }
        ui.registration = None;
        if let Some(mut child) = ui.child.take()
            && child.try_wait().ok().flatten().is_none()
        {
            let _ = child.kill();
            let _ = child.wait();
        }
        ui.pending_activation = false;
    }
}

/// 用途：检查 UI 子进程是否仍然有效，并清理失效进程。
fn ui_child_is_valid(ui: &mut UiState, pid: u32) -> bool {
    let Some(child) = ui.child.as_mut() else {
        return false;
    };
    if child.id() != pid {
        return false;
    }
    let running = match child.try_wait() {
        Ok(None) => true,
        Ok(Some(_)) | Err(_) => {
            ui.child = None;
            false
        }
    };
    if !running {
        return false;
    }
    let expected = std::fs::canonicalize(&ui.path).ok();
    let actual = process_executable_path(pid)
        .and_then(|path| std::fs::canonicalize(path).ok());
    if expected
        .as_ref()
        .zip(actual.as_ref())
        .is_some_and(|(expected, actual)| same_executable_path(expected, actual))
    {
        return true;
    }
    if let Some(mut child) = ui.child.take() {
        let _ = child.kill();
        let _ = child.wait();
    }
    false
}

#[cfg(windows)]
/// 用途：读取 Windows 进程对应的可执行文件路径。
fn process_executable_path(pid: u32) -> Option<PathBuf> {
    use windows_sys::Win32::Foundation::{CloseHandle, FALSE};
    use windows_sys::Win32::System::Threading::{
        OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
    };

    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) };
    if process.is_null() {
        return None;
    }
    let mut buffer = vec![0_u16; 32_768];
    let mut length = u32::try_from(buffer.len()).ok()?;
    let succeeded =
        unsafe { QueryFullProcessImageNameW(process, 0, buffer.as_mut_ptr(), &mut length) } != 0;
    unsafe {
        CloseHandle(process);
    }
    if !succeeded {
        return None;
    }
    buffer.truncate(usize::try_from(length).ok()?);
    Some(PathBuf::from(String::from_utf16(&buffer).ok()?))
}

#[cfg(target_os = "linux")]
/// 用途：读取 Linux 进程对应的可执行文件路径。
fn process_executable_path(pid: u32) -> Option<PathBuf> {
    std::fs::read_link(format!("/proc/{pid}/exe")).ok()
}

#[cfg(target_os = "macos")]
/// 用途：读取 macOS 进程对应的可执行文件路径。
fn process_executable_path(pid: u32) -> Option<PathBuf> {
    use std::ffi::OsString;
    use std::os::unix::ffi::OsStringExt;

    let mut buffer = vec![0_u8; libc::PROC_PIDPATHINFO_MAXSIZE as usize];
    let length = unsafe {
        libc::proc_pidpath(
            i32::try_from(pid).ok()?,
            buffer.as_mut_ptr().cast(),
            u32::try_from(buffer.len()).ok()?,
        )
    };
    if length <= 0 {
        return None;
    }
    buffer.truncate(usize::try_from(length).ok()?);
    Some(PathBuf::from(OsString::from_vec(buffer)))
}

#[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
/// 用途：在不支持的平台上返回未知的进程可执行文件路径。
fn process_executable_path(_pid: u32) -> Option<PathBuf> {
    None
}

#[cfg(windows)]
/// 用途：以 Windows 不区分大小写的规则比较可执行文件路径。
fn same_executable_path(left: &Path, right: &Path) -> bool {
    left.to_string_lossy()
        .eq_ignore_ascii_case(&right.to_string_lossy())
}

#[cfg(not(windows))]
/// 用途：按当前平台路径规则比较可执行文件路径。
fn same_executable_path(left: &Path, right: &Path) -> bool {
    left == right
}

#[async_trait::async_trait]
/// 用途：将 Agent 业务能力接入共享 IPC 方法处理接口。
impl MethodHandler for AgentHandler {
    /// 用途：分发 IPC 请求并返回对应的 Agent 操作结果。
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
                sources::source_download(source_location, user_agent, proxy_mode, &settings)
                    .await
                    .map_err(internal_error)
            }
            "sources.local.get" => {
                let path = required_string_param(&params, "path", "sources.invalid_path")?;
                let content = sources::source_read_local(path).map_err(internal_error)?;
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
                sources::provider_upload(
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
                updates::app_update_check(
                    &self.app_version,
                    channel,
                    &format!("Shadioium/{}", self.app_version),
                )
                .await
                .map_err(internal_error)
            }
            "core.update" => {
                let _maintenance = self.maintenance.lock().await;
                let current = updates::core_download_latest(
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
                    service_manager::service_command(&self.service_path, "restart-core", None)
                        .map(|_| serde_json::json!({}))
                        .map_err(internal_error)
                } else {
                    self.core
                        .handle("core.restart", serde_json::json!({}))
                        .await
                };
                if let Err(error) = restart {
                    let _ = updates::core_restore(&self.mihomo_path);
                    if tun_enabled {
                        let _ =
                            service_manager::service_command(
                                &self.service_path,
                                "restart-core",
                                None,
                            );
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
                    let _ = updates::core_restore(&self.mihomo_path);
                    if tun_enabled {
                        let _ =
                            service_manager::service_command(
                                &self.service_path,
                                "restart-core",
                                None,
                            );
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
                let _ = updates::core_backup_delete(&self.mihomo_path);
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
                let path = backups::backup_create(&self.user_data_dir, destination)
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
                let message = backups::backup_restore(&self.user_data_dir, source, merge)
                    .map_err(internal_error)?;
                Ok(serde_json::json!({ "success": true, "message": message }))
            }
            "backups.webdav.test" => {
                let settings = webdav_settings_parse(&params)?;
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
                let settings = webdav_settings_parse(&params)?;
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
                let settings = webdav_settings_parse(&params)?;
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
                let settings = webdav_settings_parse(&params)?;
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
                let settings = webdav_settings_parse(&params)?;
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
            "service.status" => Ok(service_manager::service_status(&self.service_path)),
            "service.install" => {
                let _maintenance = self.maintenance.lock().await;
                let message = service_manager::service_install(
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
                let message = service_manager::service_uninstall(
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
                let output = service_core_start(
                    &self.service_path,
                    Path::new(mihomo_path),
                    Path::new(data_core_dir),
                    Path::new(config_path),
                )
                .map_err(internal_error)?;
                Ok(service_operation_result(output))
            }
            "service.stop_core" => {
                let output =
                    service_manager::service_command(&self.service_path, "stop-core", None)
                    .map_err(internal_error)?;
                Ok(service_operation_result(output))
            }
            "service.restart_core" => {
                let output =
                    service_manager::service_command(&self.service_path, "restart-core", None)
                    .map_err(internal_error)?;
                Ok(service_operation_result(output))
            }
            "service.heartbeat" => {
                let output =
                    service_manager::service_command(&self.service_path, "heartbeat", None)
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
                        system_proxy::proxy_set(&self.user_data_dir, &settings, false)
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
                self.tun_switch(enabled).await?;
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
                system_proxy::proxy_set(&self.user_data_dir, &settings, enabled)
                    .map_err(internal_error)?;
                let mut state = self.state.lock().await;
                state.system_proxy_enabled = enabled;
                self.state_store.save(&state).map_err(internal_error)?;
                Ok(serde_json::json!({ "enabled": enabled }))
            }
            "ui.activate" => self.ui_activate().await,
            "ui.register" | "ui.unregister" => Err(ErrorBody {
                code: "ui.connection_required".into(),
                message: "UI registration requires a tracked IPC connection".into(),
            }),
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
                tun_config_write(&source, &active).map_err(internal_error)?;
                let output = service_core_start(
                    &self.service_path,
                    &self.mihomo_path,
                    &self.data_core_dir,
                    &active,
                )
                .map_err(internal_error)?;
                Ok(serde_json::json!({ "pid": pid_parse(&output) }))
            }
            "core.stop" if tun_enabled => {
                service_manager::service_command(&self.service_path, "stop-core", None)
                    .map_err(internal_error)?;
                self.state_update(false, None).await?;
                Ok(serde_json::json!({}))
            }
            "core.restart" if tun_enabled => {
                let output =
                    service_manager::service_command(&self.service_path, "restart-core", None)
                    .map_err(internal_error)?;
                self.state_update(true, None).await?;
                Ok(serde_json::json!({ "pid": pid_parse(&output) }))
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
                tun_config_write(&source, &active).map_err(internal_error)?;
                let output = service_core_start(
                    &self.service_path,
                    &self.mihomo_path,
                    &self.data_core_dir,
                    &active,
                )
                .map_err(internal_error)?;
                self.state_update(true, Some(source)).await?;
                Ok(serde_json::json!({ "mode": "restart", "pid": pid_parse(&output) }))
            }
            "core.start" => {
                let result = self.core.handle(method, params).await?;
                self.state_update(true, None).await?;
                Ok(result)
            }
            "core.stop" => {
                let result = self.core.handle(method, params).await?;
                self.state_update(false, None).await?;
                Ok(result)
            }
            "core.restart" => {
                let result = self.core.handle(method, params).await?;
                self.state_update(true, None).await?;
                Ok(result)
            }
            "core.apply_config" => {
                let runtime_yaml_path = params
                    .get("runtime_yaml_path")
                    .and_then(|value| value.as_str())
                    .map(PathBuf::from);
                let result = self.core.handle(method, params).await?;
                self.state_update(true, runtime_yaml_path).await?;
                Ok(result)
            }
            _ => self.core.handle(method, params).await,
        }
    }

    /// 用途：分发依赖连接身份的 UI 请求或普通 IPC 请求。
    async fn handle_connection(
        &self,
        connection_id: ConnectionId,
        method: &str,
        params: serde_json::Value,
    ) -> std::result::Result<serde_json::Value, ErrorBody> {
        match method {
            "ui.register" => self.ui_register(connection_id, &params).await,
            "ui.unregister" => Ok(self.ui_unregister(connection_id, &params).await),
            _ => self.handle(method, params).await,
        }
    }

    /// 用途：登记新建立的 IPC 连接。
    async fn connection_opened(&self, connection_id: ConnectionId) {
        self.active_connections.lock().await.insert(connection_id);
    }

    /// 用途：移除已断开的 IPC 连接并清理关联 UI。
    async fn connection_closed(&self, connection_id: ConnectionId) {
        let mut active_connections = self.active_connections.lock().await;
        active_connections.remove(&connection_id);
        self.ui_connection_close(connection_id).await;
    }
}

/// 用途：将内部错误转换为 IPC 可传输的错误体。
fn internal_error(error: impl std::fmt::Display) -> ErrorBody {
    ErrorBody {
        code: "agent.internal".into(),
        message: error.to_string(),
    }
}

/// 用途：从 JSON 参数中读取必需的字符串字段。
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

/// 用途：解析 WebDAV 配置参数并校验必需字段。
fn webdav_settings_parse(
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

/// 用途：构造服务操作的统一 JSON 返回值。
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

/// 启动 Agent。
fn main() -> Result<()> {
    tracing_init();
    let options = agent_options_parse()?;

    // 防止多个 agent 实例运行
    if agent_exists(&options)? {
        return Ok(());
    }

    // 启动托盘
    tray::tray_run(move |command_rx, desktop_proxy| {
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
        if let Err(error) =
            runtime.block_on(agent_run(options, command_rx, desktop_proxy.clone()))
        {
            tracing::error!("User Agent stopped with an error: {error:#}");
        }
        let _ = desktop_proxy.send_event(tray::DesktopEvent::BackendStopped);
    })
}

/// 用途：初始化 Agent 的运行时、IPC 服务和后台任务。
async fn agent_run(
    options: AgentOptions,
    mut command_rx: mpsc::UnboundedReceiver<tray::AgentCommand>,
    desktop_proxy: tao::event_loop::EventLoopProxy<tray::DesktopEvent>,
) -> Result<()> {
    let runtime_dir = options.user_data_dir.join("runtime");
    std::fs::create_dir_all(&runtime_dir).context("failed to create runtime directory")?;
    ipc_directory_prepare(&options)?;
    let _instance_lock = instance_lock_acquire(&runtime_dir)?;

    let state_store = Arc::new(AgentStateStore::new(&options.user_data_dir));
    let mut state = state_store.load();
    let subscriptions = subscriptions::SubscriptionStore::new(&options.user_data_dir);
    if let Err(error) = state_runtime_path_recover(
        &options.user_data_dir,
        &state_store,
        &subscriptions,
        &mut state,
    ) {
        tracing::warn!("failed to recover Agent runtime config: {error:#}");
    }
    let settings_store = Arc::new(Mutex::new(settings::SettingsStore::new(
        &options.user_data_dir,
    )));
    let subscription_store = Arc::new(Mutex::new(subscriptions));
    let override_store = Arc::new(Mutex::new(overrides::OverrideStore::new(
        &options.user_data_dir,
    )));
    let runtime_file_store = Arc::new(Mutex::new(runtime_files::RuntimeFileStore::new(
        &options.user_data_dir,
        &options.data_core_dir,
    )));
    let initial_settings = settings_store.lock().await.load().unwrap_or_default();
    let bootstrap_yaml = startup_yaml_load(&options, &state, &initial_settings)?;
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
        active_connections: Mutex::new(HashSet::new()),
        ui: Mutex::new(UiState {
            path: options.ui_path.clone(),
            child: None,
            registration: None,
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
    let supervisor_task = tokio::spawn(core_supervise(
        core.clone(),
        shared_state.clone(),
        events_rx,
    ));
    let service_log_task = tokio::spawn(service_core_logs_supervise(
        options.mihomo_endpoint.clone(),
        settings_store.clone(),
        events_tx.clone(),
    ));
    let service_state_task = tokio::spawn(service_core_state_supervise(
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
        outbound_mode: outbound_mode_parse(&initial_settings.outbound_mode),
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

    let startup = if initial_settings.is_tun_enabled {
        tun_host_switch(true, &options, &shared_state, &core).await
    } else {
        core.start_core().await
    };
    if let Err(error) = startup {
        tracing::error!("initial core startup failed: {error:#}");
    } else {
        agent_handler.state_update(true, None).await.map_err(|error| {
            anyhow::anyhow!("failed to persist initial core state: {}", error.message)
        })?;
        if initial_settings.is_tun_enabled {
            tray_state.core_running = true;
            let _ = desktop_proxy.send_event(tray::DesktopEvent::StateChanged(tray_state.clone()));
        }
        if !options.silent_start {
            agent_handler
                .ui_activate()
                .await
                .map_err(|error| {
                    anyhow::anyhow!("initial UI activation failed: {}", error.message)
                })?;
        }
    }

    loop {
        tokio::select! {
            signal = shutdown_signal_wait() => {
                signal.context("failed to listen for agent shutdown signal")?;
                break;
            }
            _ = agent_shutdown_rx.recv() => break,
            _ = settings_poll.tick() => {
                match settings_store.lock().await.reload_if_changed() {
                    Ok(Some(changed)) => {
                        if changed.is_tun_enabled != tray_state.tun_enabled {
                            if let Err(error) = tun_host_switch(
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
                        tray_state.outbound_mode = outbound_mode_parse(&changed.outbound_mode);
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
                            if let Err(error) = tun_host_switch(
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
                service_heartbeat_send(&options.service_path);
            }
            command = command_rx.recv() => {
                match command {
                    Some(tray::AgentCommand::ActivateUi) => {
                        let _ = agent_handler.ui_activate().await;
                    }
                    Some(tray::AgentCommand::RestartCore) => {
                        if tray_state.tun_enabled {
                            if let Err(error) = service_manager::service_command(
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
                        if let Err(error) = terminal_proxy_copy(shell, &settings) {
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
                        match system_proxy::proxy_set(&options.user_data_dir, &settings, enabled) {
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
                        match tun_host_switch(
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
                    #[cfg(windows)]
                    Some(tray::AgentCommand::SessionEnding(completed)) => {
                        host_state_cleanup(
                            &options,
                            &settings_store,
                            &shared_state,
                            &agent_handler.state_store,
                            &core,
                        ).await;
                        let _ = completed.send(());
                        break;
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

    host_state_cleanup(
        &options,
        &settings_store,
        &shared_state,
        &agent_handler.state_store,
        &core,
    )
    .await;
    supervisor_task.abort();
    service_log_task.abort();
    service_state_task.abort();
    tray_state_task.abort();
    background_task.abort();
    let _ = server_shutdown_tx.send(()).await;
    endpoint_cleanup(&options.endpoint);
    Ok(())
}

/// 用途：清理宿主状态文件、子进程和临时运行数据。
async fn host_state_cleanup(
    options: &AgentOptions,
    settings_store: &Arc<Mutex<settings::SettingsStore>>,
    shared_state: &Arc<Mutex<AgentState>>,
    state_store: &AgentStateStore,
    core: &CoreRuntime,
) {
    let current_settings = settings_store.lock().await.load().unwrap_or_default();
    if shared_state.lock().await.system_proxy_enabled {
        if let Err(error) =
            system_proxy::proxy_set(&options.user_data_dir, &current_settings, false)
        {
            tracing::warn!("failed to disable system proxy during shutdown: {error:#}");
        } else {
            let mut state = shared_state.lock().await;
            state.system_proxy_enabled = false;
            if let Err(error) = state_store.save(&state) {
                tracing::warn!("failed to persist system proxy shutdown state: {error:#}");
            }
        }
    }
    if current_settings.is_tun_enabled
        && let Err(error) =
            service_manager::service_command(&options.service_path, "stop-core", None)
    {
        tracing::warn!("failed to stop service core during exit: {error:#}");
    }
    if let Err(error) = core.stop_core().await {
        tracing::warn!("failed to stop normal core during shutdown: {error:#}");
    }
}

/// 用途：等待操作系统发出的退出信号。
async fn shutdown_signal_wait() -> Result<()> {
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

/// 用途：持续监督核心进程并在需要时恢复运行。
async fn core_supervise(
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

/// 用途：持续转发服务核心日志到 Agent 日志系统。
async fn service_core_logs_supervise(
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

/// 用途：监督服务核心状态并同步 Agent 状态。
async fn service_core_state_supervise(
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

/// 用途：选择并加载启动时应使用的 YAML 配置。
fn startup_yaml_load(
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

/// 用途：获取 Agent 单实例锁，防止重复运行。
fn instance_lock_acquire(runtime_dir: &Path) -> Result<File> {
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

/// 用途：创建 IPC 所需的目录并清理残留端点。
fn ipc_directory_prepare(options: &AgentOptions) -> Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;

        let directory = Path::new(&options.endpoint)
            .parent()
            .context("Unix Agent IPC endpoint has no parent directory")?;
        for endpoint in [&options.endpoint, &options.mihomo_endpoint] {
            if Path::new(endpoint).parent() != Some(directory) {
                bail!(
                    "Unix IPC endpoints must use the same private directory: {endpoint}"
                );
            }
        }
        match std::fs::symlink_metadata(&directory) {
            Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_dir() => {
                bail!(
                    "private IPC path is not a directory: {}",
                    directory.display()
                );
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                std::fs::create_dir(&directory).with_context(|| {
                    format!(
                        "failed to create private IPC directory: {}",
                        directory.display()
                    )
                })?;
            }
            Err(error) => {
                return Err(error).with_context(|| {
                    format!(
                        "failed to inspect private IPC directory: {}",
                        directory.display()
                    )
                });
            }
        }
        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o700))
            .with_context(|| {
                format!(
                    "failed to protect private IPC directory: {}",
                    directory.display()
                )
            })?;
    }
    #[cfg(not(unix))]
    {
        let _ = options;
    }
    Ok(())
}

/// 用途：检查是否已有 Agent 实例在运行。
fn agent_exists(options: &AgentOptions) -> Result<bool> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .context("failed to create existing Agent probe runtime")?;
    match runtime.block_on(async {
        tokio::time::timeout(
            Duration::from_secs(2),
            agent_request_forward(options),
        )
        .await
    }) {
        Ok(result) => result,
        Err(_) => Ok(false),
    }
}

/// 用途：将请求异步转发给已运行的 Agent 实例。
async fn agent_request_forward(options: &AgentOptions) -> Result<bool> {
    #[cfg(unix)]
    let name = options
        .endpoint
        .as_str()
        .to_fs_name::<GenericFilePath>()
        .context("failed to convert existing Agent Unix socket name")?;
    #[cfg(windows)]
    let name = options
        .endpoint
        .as_str()
        .to_ns_name::<GenericNamespaced>()
        .context("failed to convert existing Agent pipe name")?;
    let stream = match TokioIpcStream::connect(name).await {
        Ok(stream) => stream,
        Err(_) => return Ok(false),
    };
    let (reader, mut writer) = stream.split();
    let mut reader = BufReader::new(reader);
    agent_request_send(&mut reader, &mut writer, "startup-core", "core.start").await?;
    if !options.silent_start {
        agent_request_send(&mut reader, &mut writer, "startup-ui", "ui.activate").await?;
    }
    Ok(true)
}

/// 用途：通过 IPC 发送请求并读取响应。
async fn agent_request_send<R, W>(
    reader: &mut R,
    writer: &mut W,
    id: &str,
    method: &str,
) -> Result<()>
where
    R: AsyncBufRead + Unpin,
    W: AsyncWrite + Unpin,
{
    let request = serde_json::json!({
        "id": id,
        "method": method,
        "params": {},
    });
    writer
        .write_all(serde_json::to_string(&request)?.as_bytes())
        .await
        .context("failed to write existing Agent request")?;
    writer
        .write_all(b"\n")
        .await
        .context("failed to finish existing Agent request")?;

    let mut line = String::new();
    loop {
        line.clear();
        if reader
            .read_line(&mut line)
            .await
            .context("failed to read existing Agent response")?
            == 0
        {
            bail!("existing Agent closed the startup request");
        }
        let response: serde_json::Value =
            serde_json::from_str(line.trim_end()).context("existing Agent response is invalid")?;
        if response.get("id").and_then(serde_json::Value::as_str) != Some(id) {
            continue;
        }
        if let Some(error) = response.get("error") {
            bail!("existing Agent rejected {method}: {error}");
        }
        return Ok(());
    }
}

/// 用途：构造当前平台默认的 Agent 启动选项。
fn agent_options_default() -> Result<AgentOptions> {
    let executable = std::env::current_exe().context("failed to resolve Agent executable path")?;
    let base_dir = executable
        .parent()
        .context("Agent executable path has no parent directory")?
        .to_path_buf();
    let install_data_dir = base_dir.join("data");
    let user_data_dir = user_data_dir_default(&base_dir, &install_data_dir)?;
    let runtime_dir = user_data_dir.join("runtime");
    let pipe_prefix = option_env!("SBPROXY_PIPE_PREFIX").unwrap_or("sbproxy");
    let channel = if cfg!(debug_assertions) { "dev" } else { "prod" };
    let endpoint_name = format!("{pipe_prefix}_agent_{channel}");
    let mihomo_name = format!("{pipe_prefix}_mihomo_{channel}");
    #[cfg(windows)]
    let endpoint = endpoint_name;
    #[cfg(unix)]
    let endpoint = ipc_directory_unix(pipe_prefix)
        .join(format!("{endpoint_name}.sock"))
        .to_string_lossy()
        .into_owned();
    #[cfg(windows)]
    let mihomo_endpoint = format!(r"\\.\pipe\{mihomo_name}");
    #[cfg(unix)]
    let mihomo_endpoint = ipc_directory_unix(pipe_prefix)
        .join(format!("{mihomo_name}.sock"))
        .to_string_lossy()
        .into_owned();
    let binary_suffix = if cfg!(windows) { ".exe" } else { "" };
    let service_stem = option_env!("SBPROXY_APP_NAME").unwrap_or("sbproxyrev");
    let service_installed_suffix = if cfg!(debug_assertions) { "_dev" } else { "" };
    let ui_stem = option_env!("SBPROXY_UI_BINARY").unwrap_or("sbproxy");

    Ok(AgentOptions {
        endpoint,
        mihomo_path: install_data_dir
            .join("core")
            .join(format!("clash-mihomo-core{binary_suffix}")),
        data_core_dir: install_data_dir.join("core"),
        user_data_dir: user_data_dir.clone(),
        mihomo_endpoint,
        bootstrap_yaml_path: runtime_dir.join("_agent_bootstrap.yaml"),
        ui_path: base_dir.join(format!("{ui_stem}{binary_suffix}")),
        service_path: user_data_dir
            .join("service")
            .join(format!(
                "{service_stem}_service_host{service_installed_suffix}{binary_suffix}"
            )),
        service_update_path: install_data_dir
            .join("service")
            .join("update")
            .join(format!("{service_stem}_service{binary_suffix}")),
        install_data_dir,
        app_version: option_env!("SBPROXY_APP_VERSION")
            .unwrap_or(env!("CARGO_PKG_VERSION"))
            .to_owned(),
        silent_start: false,
    })
}

#[cfg(unix)]
/// 用途：计算 Unix 平台 IPC 目录路径。
fn ipc_directory_unix(pipe_prefix: &str) -> PathBuf {
    // SAFETY: geteuid has no side effects and returns the current process effective user ID.
    let effective_uid = unsafe { libc::geteuid() };
    PathBuf::from("/tmp").join(format!("{pipe_prefix}-ipc-{effective_uid}"))
}

/// 用途：根据平台目录推导用户数据目录。
fn user_data_dir_default(base_dir: &Path, install_data_dir: &Path) -> Result<PathBuf> {
    #[cfg(target_os = "linux")]
    if let Some(configured) = std::env::var_os("PORTABLE_APP_DATA_DIR") {
        let configured = PathBuf::from(configured);
        return if configured.is_absolute() {
            Ok(configured)
        } else {
            Ok(std::env::current_dir()
                .context("failed to resolve current directory")?
                .join(configured))
        };
    }

    #[cfg(target_os = "macos")]
    {
        if let Some(bundle_dir) = bundle_dir_macos(base_dir) {
            let identifier = option_env!("SBPROXY_BUNDLE_IDENTIFIER").unwrap_or(
                if cfg!(debug_assertions) {
                    "com.sbproxy.sbproxy.dev"
                } else {
                    "com.sbproxy.sbproxy"
                },
            );
            if bundle_dir.starts_with("/Applications") {
                if let Some(home) = std::env::var_os("HOME") {
                    return Ok(PathBuf::from(home)
                        .join("Library")
                        .join("Application Support")
                        .join(identifier));
                }
            } else if let Some(bundle_parent) = bundle_dir.parent() {
                return Ok(bundle_parent.join(format!("{identifier}.data")));
            }
        }
    }

    let _ = base_dir;
    Ok(install_data_dir.to_path_buf())
}

#[cfg(target_os = "macos")]
/// 用途：查找 macOS 应用包目录。
fn bundle_dir_macos(base_dir: &Path) -> Option<&Path> {
    let contents = base_dir.parent()?;
    let bundle = contents.parent()?;
    (base_dir.file_name()? == "MacOS"
        && contents.file_name()? == "Contents"
        && bundle.extension().is_some_and(|extension| extension == "app"))
    .then_some(bundle)
}

/// 用途：解析命令行参数为 Agent 启动选项。
fn agent_options_parse() -> Result<AgentOptions> {
    let mut options = agent_options_default()?;
    for argument in std::env::args().skip(1) {
        match argument.as_str() {
            "--silent-start" => options.silent_start = true,
            _ => bail!("unexpected argument: {argument}"),
        }
    }
    Ok(options)
}

/// 用途：初始化结构化日志输出。
fn tracing_init() {
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info"));
    let _ = tracing_subscriber::fmt().with_env_filter(filter).try_init();
}

/// 用途：删除指定 IPC 端点及其残留文件。
fn endpoint_cleanup(endpoint: &str) {
    #[cfg(unix)]
    {
        let _ = std::fs::remove_file(endpoint);
    }
    #[cfg(windows)]
    {
        let _ = endpoint;
    }
}

/// 用途：将字符串形式的出站模式解析为枚举值。
fn outbound_mode_parse(value: &str) -> tray::OutboundMode {
    match value.to_ascii_lowercase().as_str() {
        "global" => tray::OutboundMode::Global,
        "direct" => tray::OutboundMode::Direct,
        _ => tray::OutboundMode::Rule,
    }
}

/// 用途：返回出站模式的稳定字符串名称。
fn outbound_mode_name(mode: tray::OutboundMode) -> &'static str {
    match mode {
        tray::OutboundMode::Rule => "Rule",
        tray::OutboundMode::Global => "Global",
        tray::OutboundMode::Direct => "Direct",
    }
}

/// 用途：复制终端代理配置到目标运行环境。
fn terminal_proxy_copy(
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

/// 用途：向服务发送心跳，维持服务端状态同步。
fn service_heartbeat_send(service_path: &Path) {
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

/// 用途：处理宿主侧的 TUN 切换请求。
async fn tun_host_switch(
    enabled: bool,
    options: &AgentOptions,
    state: &Arc<Mutex<AgentState>>,
    core: &Arc<CoreRuntime>,
) -> Result<()> {
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
    tun_core_switch(
        enabled,
        &options.service_path,
        &options.mihomo_path,
        &options.data_core_dir,
        &source,
        &active,
        core,
    )
    .await
}

/// 用途：执行 IPC 与托盘共用的 TUN 核心切换操作。
async fn tun_core_switch(
    enabled: bool,
    service_path: &Path,
    mihomo_path: &Path,
    data_core_dir: &Path,
    source: &Path,
    active: &Path,
    core: &CoreRuntime,
) -> Result<()> {
    if !enabled {
        service_manager::service_command(service_path, "stop-core", None)?;
        core.start_core().await?;
        return Ok(());
    }
    if !service_path.is_file() {
        bail!(
            "service executable was not found: {}",
            service_path.display()
        );
    }
    tun_config_write(source, active)?;
    core.stop_core().await?;
    if let Err(error) = service_core_start(
        service_path,
        mihomo_path,
        data_core_dir,
        active,
    ) {
        let _ = core.start_core().await;
        return Err(error);
    }
    Ok(())
}

/// 用途：根据源配置生成启用 TUN 的配置文件。
fn tun_config_write(source: &Path, destination: &Path) -> Result<()> {
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
        serde_yaml_ng::Value::Bool(true),
    );
    let parent = destination.parent().context("TUN config has no parent")?;
    std::fs::create_dir_all(parent)?;
    std::fs::write(destination, serde_yaml_ng::to_string(&yaml)?)?;
    Ok(())
}

/// 用途：启动服务托管的核心进程。
fn service_core_start(
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
    service_manager::service_command(service_path, "start-core", Some(&payload))
}

/// 用途：从服务输出中提取进程 ID。
fn pid_parse(output: &str) -> Option<u32> {
    output.split_whitespace().find_map(|part| {
        part.strip_prefix("pid=")
            .and_then(|value| value.parse().ok())
    })
}

/// 用途：读取服务核心的运行状态快照。
fn service_core_snapshot(service_path: &Path, endpoint: &str) -> Result<serde_json::Value> {
    let output = service_manager::service_command(service_path, "status", None)?;
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

    /// 用途：提供测试临时目录的创建接口。
    impl TestDirectory {
        /// 用途：创建当前测试独占的临时目录。
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

    /// 用途：在测试目录离开作用域时清理临时文件。
    impl Drop for TestDirectory {
        /// 用途：删除测试创建的临时目录。
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    /// 用途：验证 Agent 状态可以完整保存并重新加载。
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
    /// 用途：验证缺少 Agent 配置路径时会从当前订阅恢复并持久化。
    fn state_runtime_path_recovers_selected_subscription_config() -> Result<()> {
        let directory = TestDirectory::create("state-runtime-recover")?;
        let state_store = AgentStateStore::new(&directory.0);
        let subscription_store = subscriptions::SubscriptionStore::new(&directory.0);
        let subscription_id = "selected-subscription";
        subscription_store.set_current_subscription_id(Some(subscription_id))?;
        let runtime_path = directory
            .0
            .join("runtime")
            .join(subscription_id)
            .join("runtime.yaml");
        std::fs::create_dir_all(
            runtime_path
                .parent()
                .context("runtime path has no parent")?,
        )?;
        std::fs::write(&runtime_path, "mixed-port: 7890\nproxy-groups: []\n")?;
        let mut state = AgentState::default();

        let recovered = state_runtime_path_recover(
            &directory.0,
            &state_store,
            &subscription_store,
            &mut state,
        )?;

        assert!(recovered);
        assert_eq!(
            state.runtime_yaml_path.as_deref(),
            Some(runtime_path.as_path())
        );
        assert_eq!(
            state_store.load().runtime_yaml_path.as_deref(),
            Some(runtime_path.as_path())
        );
        Ok(())
    }

    #[test]
    /// 用途：验证已保存路径失效时会改用当前订阅仍存在的配置。
    fn state_runtime_path_repairs_stale_path() -> Result<()> {
        let directory = TestDirectory::create("state-runtime-stale")?;
        let state_store = AgentStateStore::new(&directory.0);
        let subscription_store = subscriptions::SubscriptionStore::new(&directory.0);
        let subscription_id = "selected-subscription";
        subscription_store.set_current_subscription_id(Some(subscription_id))?;
        let runtime_path = directory
            .0
            .join("runtime")
            .join(subscription_id)
            .join("runtime.yaml");
        std::fs::create_dir_all(
            runtime_path
                .parent()
                .context("runtime path has no parent")?,
        )?;
        std::fs::write(&runtime_path, "mixed-port: 7890\n")?;
        let mut state = AgentState {
            runtime_yaml_path: Some(directory.0.join("missing-runtime.yaml")),
            ..AgentState::default()
        };

        let recovered = state_runtime_path_recover(
            &directory.0,
            &state_store,
            &subscription_store,
            &mut state,
        )?;

        assert!(recovered);
        assert_eq!(
            state.runtime_yaml_path.as_deref(),
            Some(runtime_path.as_path())
        );
        Ok(())
    }

    #[test]
    /// 用途：验证没有当前订阅时保持原状态并继续使用空引导配置。
    fn state_runtime_path_keeps_state_without_selection() -> Result<()> {
        let directory = TestDirectory::create("state-runtime-no-selection")?;
        let state_store = AgentStateStore::new(&directory.0);
        let subscription_store = subscriptions::SubscriptionStore::new(&directory.0);
        let mut state = AgentState::default();

        let recovered = state_runtime_path_recover(
            &directory.0,
            &state_store,
            &subscription_store,
            &mut state,
        )?;

        assert!(!recovered);
        assert!(state.runtime_yaml_path.is_none());
        Ok(())
    }

    #[test]
    /// 用途：验证当前订阅缺少运行配置时安全回退且不清除选择。
    fn state_runtime_path_keeps_selection_when_config_is_missing() -> Result<()> {
        let directory = TestDirectory::create("state-runtime-missing")?;
        let state_store = AgentStateStore::new(&directory.0);
        let subscription_store = subscriptions::SubscriptionStore::new(&directory.0);
        let subscription_id = "selected-subscription";
        subscription_store.set_current_subscription_id(Some(subscription_id))?;
        let mut state = AgentState::default();

        let recovered = state_runtime_path_recover(
            &directory.0,
            &state_store,
            &subscription_store,
            &mut state,
        )?;

        assert!(!recovered);
        assert!(state.runtime_yaml_path.is_none());
        assert_eq!(
            subscription_store.current_subscription_id()?.as_deref(),
            Some(subscription_id)
        );
        Ok(())
    }

    #[test]
    /// 用途：验证非法订阅标识不会被拼接为目录穿越路径。
    fn state_runtime_path_rejects_parent_directory_subscription_id() -> Result<()> {
        let directory = TestDirectory::create("state-runtime-invalid-id")?;
        let state_store = AgentStateStore::new(&directory.0);
        let subscription_store = subscriptions::SubscriptionStore::new(&directory.0);
        let selection_path = directory
            .0
            .join("subscriptions")
            .join("selection_state.json");
        std::fs::create_dir_all(
            selection_path
                .parent()
                .context("selection path has no parent")?,
        )?;
        std::fs::write(&selection_path, r#"{"CurrentSubscriptionId":".."}"#)?;
        let mut state = AgentState::default();

        let result =
            state_runtime_path_recover(&directory.0, &state_store, &subscription_store, &mut state);

        assert!(result.is_err());
        assert!(state.runtime_yaml_path.is_none());
        Ok(())
    }

    #[test]
    /// 用途：验证启动配置优先使用最后应用的运行时配置。
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
            silent_start: false,
        };
        let state = AgentState {
            desired_running: true,
            runtime_yaml_path: Some(runtime_path),
            system_proxy_enabled: false,
        };

        let yaml = startup_yaml_load(&options, &state, &settings::AgentSettings::default())?;

        assert_eq!(yaml, "mixed-port: 7891");
        Ok(())
    }
}
