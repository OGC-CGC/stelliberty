use std::path::{Path, PathBuf};
use std::time::SystemTime;

use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::storage;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
/// 用途：表示 Agent 当前生效的应用设置。
pub struct AgentSettings {
    pub settings_schema_version: u32,
    pub language: String,
    pub theme: String,
    pub accent_color_mode: String,
    pub accent_color: String,
    pub window_effect: String,
    pub window_width: Option<f64>,
    pub window_height: Option<f64>,
    pub is_window_maximized: bool,
    pub is_silent_start_enabled: bool,
    pub is_minimize_to_tray_enabled: bool,
    pub is_tray_double_click_enabled: bool,
    pub is_lazy_mode_enabled: bool,
    pub is_title_bar_fps_visible: bool,
    pub is_auto_start_enabled: bool,
    pub window_toggle_hotkey: String,
    pub system_proxy_toggle_hotkey: String,
    pub tun_toggle_hotkey: String,
    pub is_auto_check_update_enabled: bool,
    pub app_update_check_interval: String,
    pub app_update_channel: String,
    pub ignored_update_version: String,
    pub is_web_dav_backup_enabled: bool,
    pub web_dav_url: String,
    pub web_dav_user_name: String,
    pub web_dav_password: String,
    pub web_dav_remote_directory: String,
    pub web_dav_backup_interval_hours: i32,
    pub web_dav_backup_retention_count: i32,
    pub last_web_dav_backup_time: Option<String>,
    pub last_core_version: String,
    pub last_app_update_check_time: Option<String>,
    pub is_unified_delay_enabled: bool,
    pub outbound_mode: String,
    pub proxy_node_sort_mode: String,
    pub delay_test_url: String,
    pub is_allow_lan_enabled: bool,
    pub lan_authentication_user_name: String,
    pub lan_authentication_password: String,
    pub lan_allowed_ips: Vec<String>,
    pub lan_disallowed_ips: Vec<String>,
    pub skip_auth_prefixes: Vec<String>,
    pub is_ipv6_enabled: bool,
    pub is_tcp_concurrent_enabled: bool,
    pub mixed_port: i32,
    pub socks_port: Option<i32>,
    pub http_port: Option<i32>,
    pub is_external_controller_enabled: bool,
    pub external_controller_address: String,
    pub external_controller_secret: String,
    pub proxy_host: String,
    pub system_proxy_bypass: String,
    pub is_pac_mode_enabled: bool,
    pub pac_script: String,
    pub is_tun_enabled: bool,
    pub tun_stack: String,
    pub tun_device: String,
    pub is_tun_auto_route_enabled: bool,
    pub is_tun_auto_redirect_enabled: bool,
    pub is_tun_auto_detect_interface_enabled: bool,
    pub is_tun_strict_route_enabled: bool,
    pub tun_dns_hijack: Vec<String>,
    pub tun_route_exclude_addresses: Vec<String>,
    pub is_tun_icmp_forwarding_disabled: bool,
    pub tun_mtu: Option<i32>,
    pub is_dns_override_enabled: bool,
    pub is_dns_enabled: bool,
    pub dns_listen: String,
    pub dns_enhanced_mode: String,
    pub fake_ip_range: String,
    pub is_dns_respect_rules_enabled: bool,
    pub name_servers: Vec<String>,
    pub fallback_name_servers: Vec<String>,
    pub proxy_server_name_servers: Vec<String>,
    pub default_name_servers: Vec<String>,
    pub fake_ip_filters: Vec<String>,
    pub fallback_filter_geo_ip_code: String,
    pub hosts: Vec<String>,
    pub is_dns_ipv6_enabled: bool,
    pub is_dns_use_hosts_enabled: bool,
    pub is_dns_use_system_hosts_enabled: bool,
    pub direct_name_servers: Vec<String>,
    pub name_server_policy: Vec<String>,
    pub is_dns_prefer_h3_enabled: bool,
    pub fake_ip_filter_mode: String,
    pub is_direct_name_server_follow_policy_enabled: bool,
    pub is_fallback_filter_geo_ip_enabled: bool,
    pub fallback_filter_ip_cidrs: Vec<String>,
    pub fallback_filter_domains: Vec<String>,
    pub geo_data_loader: String,
    pub find_process_mode: String,
    pub is_tcp_keep_alive_enabled: bool,
    pub tcp_keep_alive_interval: i32,
    pub core_log_level: String,
}

/// 用途：提供完整且可序列化的 Agent 设置默认值。
impl Default for AgentSettings {
    /// 用途：构造首次启动或缺失字段时使用的默认设置。
    fn default() -> Self {
        Self {
            settings_schema_version: 1,
            language: "System".into(),
            theme: "System".into(),
            accent_color_mode: "System".into(),
            accent_color: "#3B82F6".into(),
            window_effect: "None".into(),
            window_width: None,
            window_height: None,
            is_window_maximized: false,
            is_silent_start_enabled: false,
            is_minimize_to_tray_enabled: false,
            is_tray_double_click_enabled: true,
            is_lazy_mode_enabled: false,
            is_title_bar_fps_visible: true,
            is_auto_start_enabled: false,
            window_toggle_hotkey: String::new(),
            system_proxy_toggle_hotkey: String::new(),
            tun_toggle_hotkey: String::new(),
            is_auto_check_update_enabled: false,
            app_update_check_interval: "startup".into(),
            app_update_channel: "stable".into(),
            ignored_update_version: String::new(),
            is_web_dav_backup_enabled: false,
            web_dav_url: String::new(),
            web_dav_user_name: String::new(),
            web_dav_password: String::new(),
            web_dav_remote_directory: "sbproxyrev-backups".into(),
            web_dav_backup_interval_hours: 24,
            web_dav_backup_retention_count: 5,
            last_web_dav_backup_time: None,
            last_core_version: String::new(),
            last_app_update_check_time: None,
            is_unified_delay_enabled: false,
            outbound_mode: "Rule".into(),
            proxy_node_sort_mode: "Default".into(),
            delay_test_url: "https://www.gstatic.com/generate_204".into(),
            is_allow_lan_enabled: false,
            lan_authentication_user_name: String::new(),
            lan_authentication_password: String::new(),
            lan_allowed_ips: Vec::new(),
            lan_disallowed_ips: Vec::new(),
            skip_auth_prefixes: Vec::new(),
            is_ipv6_enabled: false,
            is_tcp_concurrent_enabled: false,
            mixed_port: if cfg!(debug_assertions) { 2000 } else { 8888 },
            socks_port: None,
            http_port: None,
            is_external_controller_enabled: false,
            external_controller_address: if cfg!(debug_assertions) {
                "127.0.0.1:1999".into()
            } else {
                "127.0.0.1:9090".into()
            },
            external_controller_secret: String::new(),
            proxy_host: "127.0.0.1".into(),
            system_proxy_bypass: String::new(),
            is_pac_mode_enabled: false,
            pac_script: String::new(),
            is_tun_enabled: false,
            tun_stack: "Mixed".into(),
            tun_device: "clash".into(),
            is_tun_auto_route_enabled: true,
            is_tun_auto_redirect_enabled: true,
            is_tun_auto_detect_interface_enabled: true,
            is_tun_strict_route_enabled: true,
            tun_dns_hijack: vec!["any:53".into()],
            tun_route_exclude_addresses: Vec::new(),
            is_tun_icmp_forwarding_disabled: false,
            tun_mtu: Some(9000),
            is_dns_override_enabled: false,
            is_dns_enabled: true,
            dns_listen: ":53".into(),
            dns_enhanced_mode: "fake-ip".into(),
            fake_ip_range: "198.18.0.1/16".into(),
            is_dns_respect_rules_enabled: false,
            name_servers: vec![
                "8.8.8.8".into(),
                "https://doh.pub/dns-query".into(),
                "https://dns.alidns.com/dns-query".into(),
            ],
            fallback_name_servers: Vec::new(),
            proxy_server_name_servers: vec![
                "https://doh.pub/dns-query".into(),
                "https://dns.alidns.com/dns-query".into(),
                "tls://223.5.5.5".into(),
            ],
            default_name_servers: vec![
                "system".into(),
                "223.6.6.6".into(),
                "8.8.8.8".into(),
                "2400:3200::1".into(),
                "2001:4860:4860::8888".into(),
            ],
            fake_ip_filters: vec![
                "*.lan".into(),
                "*.local".into(),
                "*.arpa".into(),
                "time.*.com".into(),
                "ntp.*.com".into(),
                "+.market.xiaomi.com".into(),
                "localhost.ptlogin2.qq.com".into(),
                "*.msftncsi.com".into(),
                "www.msftconnecttest.com".into(),
            ],
            fallback_filter_geo_ip_code: "CN".into(),
            hosts: Vec::new(),
            is_dns_ipv6_enabled: true,
            is_dns_use_hosts_enabled: false,
            is_dns_use_system_hosts_enabled: false,
            direct_name_servers: Vec::new(),
            name_server_policy: Vec::new(),
            is_dns_prefer_h3_enabled: false,
            fake_ip_filter_mode: "blacklist".into(),
            is_direct_name_server_follow_policy_enabled: false,
            is_fallback_filter_geo_ip_enabled: true,
            fallback_filter_ip_cidrs: vec!["240.0.0.0/4".into(), "0.0.0.0/32".into()],
            fallback_filter_domains: vec![
                "+.google.com".into(),
                "+.facebook.com".into(),
                "+.youtube.com".into(),
            ],
            geo_data_loader: "standard".into(),
            find_process_mode: "off".into(),
            is_tcp_keep_alive_enabled: false,
            tcp_keep_alive_interval: 30,
            core_log_level: "silent".into(),
        }
    }
}

/// 用途：负责设置文件的读取、规范化、修改和持久化。
pub struct SettingsStore {
    path: PathBuf,
    modified: Option<SystemTime>,
    current: AgentSettings,
    document: Map<String, Value>,
    revision: u64,
    pending_change: bool,
}

/// 用途：提供设置文档的加载、快照、增量更新和持久化接口。
impl SettingsStore {
    /// 用途：根据用户数据目录创建尚未加载的设置存储。
    pub fn new(user_data_dir: &Path) -> Self {
        Self {
            path: user_data_dir.join("settings.json"),
            modified: None,
            current: AgentSettings::default(),
            document: Map::new(),
            revision: 0,
            pending_change: false,
        }
    }

    /// 用途：首次加载并规范化设置，后续调用返回当前副本。
    pub fn load(&mut self) -> Result<AgentSettings> {
        if self.revision != 0 {
            return Ok(self.current.clone());
        }
        let document = self.read_document()?;
        let (document, current) = settings_normalize(document)?;
        self.document = document;
        self.current = current;
        if !self.path.is_file() {
            self.write_current_document()?;
        } else {
            self.modified = std::fs::metadata(&self.path)
                .and_then(|metadata| metadata.modified())
                .ok();
        }
        self.revision = 1;
        Ok(self.current.clone())
    }

    /// 用途：检测外部修改，并在设置变化时返回新的设置副本。
    pub fn reload_if_changed(&mut self) -> Result<Option<AgentSettings>> {
        self.refresh()?;
        if !self.pending_change {
            return Ok(None);
        }
        self.pending_change = false;
        Ok(Some(self.current.clone()))
    }

    /// 用途：根据文件修改时间刷新内存中的设置文档。
    pub fn refresh(&mut self) -> Result<()> {
        if self.revision == 0 {
            self.load()?;
            return Ok(());
        }
        let modified = std::fs::metadata(&self.path)
            .and_then(|metadata| metadata.modified())
            .ok();
        if modified == self.modified {
            return Ok(());
        }
        let document = self.read_document()?;
        let (document, current) = settings_normalize(document)?;
        self.document = document;
        self.current = current;
        self.modified = modified;
        self.revision = self.revision.saturating_add(1);
        self.pending_change = true;
        Ok(())
    }

    /// 用途：返回当前设置文档的单调递增修订号。
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// 用途：刷新并返回当前修订号及完整设置文档。
    pub fn snapshot(&mut self) -> Result<(u64, Value)> {
        self.refresh()?;
        Ok((self.revision, Value::Object(self.document.clone())))
    }

    /// 用途：合并字段补丁、规范化结果并持久化新的设置文档。
    pub fn apply_patch(&mut self, changes: &Map<String, Value>) -> Result<(u64, AgentSettings, Value)> {
        if self.revision == 0 {
            self.load()?;
        }
        let mut document = self.document.clone();
        for (name, value) in changes {
            if value.is_null() {
                document.remove(name);
            } else {
                document.insert(name.clone(), value.clone());
            }
        }
        let (document, current) = settings_normalize(document)?;
        self.document = document;
        self.current = current.clone();
        self.revision = self.revision.saturating_add(1);
        self.write_current_document()?;
        self.pending_change = true;
        Ok((
            self.revision,
            current,
            Value::Object(self.document.clone()),
        ))
    }

    /// 用途：更新指定字符串设置并返回新的设置快照。
    pub fn update_string(&mut self, name: &str, value: &str) -> Result<AgentSettings> {
        let changes = Map::from_iter([(name.into(), Value::String(value.into()))]);
        self.apply_patch(&changes).map(|(_, settings, _)| settings)
    }

    /// 用途：更新指定布尔设置并返回新的设置快照。
    pub fn update_bool(&mut self, name: &str, value: bool) -> Result<AgentSettings> {
        let changes = Map::from_iter([(name.into(), Value::Bool(value))]);
        self.apply_patch(&changes).map(|(_, settings, _)| settings)
    }

    /// 用途：读取设置 JSON 对象，并在内容损坏时备份后回退为空对象。
    fn read_document(&self) -> Result<Map<String, Value>> {
        let content = match std::fs::read_to_string(&self.path) {
            Ok(content) => content,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Map::new()),
            Err(error) => return Err(error).context("failed to read settings.json"),
        };
        match serde_json::from_str::<Value>(&content) {
            Ok(Value::Object(document)) => Ok(document),
            Ok(_) => bail!("settings.json root must be an object"),
            Err(error) => {
                let backup = self.path.with_extension("json.corrupt");
                if backup.is_file()
                    && let Err(backup_error) = std::fs::remove_file(&backup)
                {
                    tracing::warn!(
                        "failed to remove previous invalid settings backup: {backup_error}"
                    );
                }
                if let Err(backup_error) = std::fs::rename(&self.path, &backup) {
                    tracing::warn!(
                        "failed to back up invalid settings.json: {backup_error}"
                    );
                }
                tracing::warn!("invalid settings.json was replaced with defaults: {error}");
                Ok(Map::new())
            }
        }
    }

    /// 用途：以统一原子写入方式保存当前设置文档并更新修改时间。
    fn write_current_document(&mut self) -> Result<()> {
        storage::file_write_atomic(&self.path, &serde_json::to_vec_pretty(&self.document)?)?;
        self.modified = std::fs::metadata(&self.path)
            .and_then(|metadata| metadata.modified())
            .ok();
        Ok(())
    }
}

/// 用途：补齐和校验设置字段，返回规范文档及强类型设置。
fn settings_normalize(
    mut document: Map<String, Value>,
) -> Result<(Map<String, Value>, AgentSettings)> {
    let defaults = serde_json::to_value(AgentSettings::default())?
        .as_object()
        .cloned()
        .context("default settings must serialize to an object")?;
    for (name, value) in defaults {
        if document.get(&name).is_none_or(Value::is_null) {
            document.insert(name, value);
        }
    }

    let channel = document
        .get("AppUpdateChannel")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase();
    document.insert(
        "AppUpdateChannel".into(),
        Value::String(if matches!(channel.as_str(), "stable" | "beta") {
            channel
        } else {
            "stable".into()
        }),
    );
    if document
        .get("WebDavBackupIntervalHours")
        .and_then(Value::as_i64)
        .is_none_or(|value| value <= 0)
    {
        document.insert("WebDavBackupIntervalHours".into(), Value::from(24));
    }
    if document
        .get("WebDavBackupRetentionCount")
        .and_then(Value::as_i64)
        .is_none_or(|value| value <= 0)
    {
        document.insert("WebDavBackupRetentionCount".into(), Value::from(5));
    }

    let current = serde_json::from_value(Value::Object(document.clone()))
        .context("settings.json contains invalid field values")?;
    Ok((document, current))
}
