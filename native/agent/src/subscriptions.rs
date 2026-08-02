use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use serde_json::{Map, Value};

use crate::storage::{entity_id_validate, file_write_atomic, json_read_or_default};

/// 用途：管理订阅清单、订阅内容、当前订阅和代理选择。
pub struct SubscriptionStore {
    subscriptions_dir: PathBuf,
    list_path: PathBuf,
    subscription_selection_path: PathBuf,
    proxy_selection_path: PathBuf,
}

/// 用途：提供订阅实体、内容和选择状态的一致持久化接口。
impl SubscriptionStore {
    /// 用途：根据用户数据目录创建订阅存储。
    pub fn new(user_data_dir: &Path) -> Self {
        Self {
            subscriptions_dir: user_data_dir.join("subscriptions"),
            list_path: user_data_dir
                .join("subscriptions")
                .join("subscriptions_list.json"),
            subscription_selection_path: user_data_dir
                .join("subscriptions")
                .join("selection_state.json"),
            proxy_selection_path: user_data_dir
                .join("proxies")
                .join("selection_state.json"),
        }
    }

    /// 用途：读取并规范化全部订阅实体。
    pub fn list(&self) -> Result<Vec<Value>> {
        let document = json_read_or_default(
            &self.list_path,
            serde_json::json!({ "Subscriptions": [] }),
            "subscription",
        )?;
        let subscriptions = document
            .get("Subscriptions")
            .and_then(Value::as_array)
            .context("subscription list must contain a Subscriptions array")?;
        subscriptions
            .iter()
            .cloned()
            .map(subscription_normalize)
            .collect()
    }

    /// 用途：新增或替换订阅实体及其内容文件。
    pub fn save(&self, subscription: Value, content: &str) -> Result<()> {
        let subscription = subscription_normalize(subscription)?;
        let id = subscription_id(&subscription)?.to_owned();
        file_write_atomic(&self.content_path(&id)?, content.as_bytes())?;
        let mut subscriptions = self.list()?;
        if let Some(index) = subscriptions
            .iter()
            .position(|candidate| subscription_id(candidate).ok() == Some(id.as_str()))
        {
            subscriptions[index] = subscription;
        } else {
            subscriptions.push(subscription);
        }
        self.write_list(&subscriptions)
    }

    /// 用途：更新已经存在的订阅实体元数据。
    pub fn update(&self, subscription: Value) -> Result<()> {
        let subscription = subscription_normalize(subscription)?;
        let id = subscription_id(&subscription)?.to_owned();
        let mut subscriptions = self.list()?;
        let index = subscriptions
            .iter()
            .position(|candidate| subscription_id(candidate).ok() == Some(id.as_str()))
            .with_context(|| format!("subscription not found: {id}"))?;
        subscriptions[index] = subscription;
        self.write_list(&subscriptions)
    }

    /// 用途：按调用方给出的完整列表保存订阅顺序。
    pub fn reorder(&self, subscriptions: &[Value]) -> Result<()> {
        let subscriptions = subscriptions
            .iter()
            .cloned()
            .map(subscription_normalize)
            .collect::<Result<Vec<_>>>()?;
        self.write_list(&subscriptions)
    }

    /// 用途：读取指定订阅的原始 YAML 内容。
    pub fn read_content(&self, subscription_id: &str) -> Result<String> {
        let path = self.content_path(subscription_id)?;
        std::fs::read_to_string(&path)
            .with_context(|| format!("failed to read subscription content: {}", path.display()))
    }

    /// 用途：原子保存指定订阅的 YAML 内容。
    pub fn save_content(&self, subscription_id: &str, content: &str) -> Result<()> {
        let path = self.content_path(subscription_id)?;
        file_write_atomic(&path, content.as_bytes())
    }

    /// 用途：返回通过校验的订阅内容文件路径。
    pub fn content_path(&self, subscription_id: &str) -> Result<PathBuf> {
        entity_id_validate(subscription_id, "subscription")?;
        Ok(self
            .subscriptions_dir
            .join(format!("{subscription_id}.yaml")))
    }

    /// 用途：删除订阅实体及其内容文件。
    pub fn delete(&self, id: &str) -> Result<()> {
        entity_id_validate(id, "subscription")?;
        let subscriptions = self
            .list()?
            .into_iter()
            .filter(|subscription| subscription_id(subscription).ok() != Some(id))
            .collect::<Vec<_>>();
        self.write_list(&subscriptions)?;
        let path = self.content_path(id)?;
        match std::fs::remove_file(&path) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => {
                return Err(error).with_context(|| {
                    format!("failed to delete subscription content: {}", path.display())
                });
            }
        }
        Ok(())
    }

    /// 用途：读取当前选中订阅的可选标识。
    pub fn current_subscription_id(&self) -> Result<Option<String>> {
        let document = json_read_or_default(
            &self.subscription_selection_path,
            serde_json::json!({ "CurrentSubscriptionId": null }),
            "subscription selection",
        )?;
        Ok(document
            .get("CurrentSubscriptionId")
            .and_then(Value::as_str)
            .map(str::to_owned))
    }

    /// 用途：更新或清除当前选中的订阅标识。
    pub fn set_current_subscription_id(&self, subscription_id: Option<&str>) -> Result<()> {
        if let Some(subscription_id) = subscription_id {
            entity_id_validate(subscription_id, "subscription")?;
        }
        file_write_atomic(
            &self.subscription_selection_path,
            &serde_json::to_vec_pretty(&serde_json::json!({
                "CurrentSubscriptionId": subscription_id,
            }))?,
        )
    }

    /// 用途：读取指定订阅的全部代理组选择。
    pub fn proxy_selections(&self, subscription_id: &str) -> Result<Map<String, Value>> {
        entity_id_validate(subscription_id, "subscription")?;
        let document = self.read_proxy_selection_document()?;
        Ok(document
            .get("Subscriptions")
            .and_then(Value::as_object)
            .and_then(|subscriptions| subscriptions.get(subscription_id))
            .and_then(Value::as_object)
            .cloned()
            .unwrap_or_default())
    }

    /// 用途：保存指定订阅和代理组的代理选择。
    pub fn set_proxy_selection(
        &self,
        subscription_id: &str,
        group_name: &str,
        proxy_name: &str,
    ) -> Result<()> {
        entity_id_validate(subscription_id, "subscription")?;
        if group_name.trim().is_empty() || proxy_name.trim().is_empty() {
            bail!("proxy group and proxy names must not be empty");
        }
        let mut document = self.read_proxy_selection_document()?;
        let subscriptions = document
            .entry("Subscriptions")
            .or_insert_with(|| Value::Object(Map::new()))
            .as_object_mut()
            .context("proxy selection subscriptions must be an object")?;
        let selections = subscriptions
            .entry(subscription_id)
            .or_insert_with(|| Value::Object(Map::new()))
            .as_object_mut()
            .context("proxy selections must be an object")?;
        selections.insert(group_name.into(), Value::String(proxy_name.into()));
        self.write_proxy_selection_document(&document)
    }

    /// 用途：删除指定订阅和代理组的代理选择。
    pub fn remove_proxy_selection(
        &self,
        subscription_id: &str,
        group_name: &str,
    ) -> Result<()> {
        entity_id_validate(subscription_id, "subscription")?;
        if group_name.trim().is_empty() {
            return Ok(());
        }
        let mut document = self.read_proxy_selection_document()?;
        let Some(subscriptions) = document
            .get_mut("Subscriptions")
            .and_then(Value::as_object_mut)
        else {
            return Ok(());
        };
        let remove_subscription = if let Some(selections) = subscriptions
            .get_mut(subscription_id)
            .and_then(Value::as_object_mut)
        {
            selections.remove(group_name);
            selections.is_empty()
        } else {
            false
        };
        if remove_subscription {
            subscriptions.remove(subscription_id);
        }
        self.write_proxy_selection_document(&document)
    }

    /// 用途：原子保存订阅列表文档。
    fn write_list(&self, subscriptions: &[Value]) -> Result<()> {
        file_write_atomic(
            &self.list_path,
            &serde_json::to_vec_pretty(&serde_json::json!({
                "Subscriptions": subscriptions,
            }))?,
        )
    }

    /// 用途：读取代理选择状态并保证根节点为对象。
    fn read_proxy_selection_document(&self) -> Result<Map<String, Value>> {
        json_read_or_default(
            &self.proxy_selection_path,
            serde_json::json!({ "Subscriptions": {} }),
            "proxy selection",
        )?
        .as_object()
        .cloned()
        .context("proxy selection state must be an object")
    }

    /// 用途：原子保存代理选择状态文档。
    fn write_proxy_selection_document(&self, document: &Map<String, Value>) -> Result<()> {
        file_write_atomic(
            &self.proxy_selection_path,
            &serde_json::to_vec_pretty(document)?,
        )
    }
}

/// 用途：提取并校验订阅实体标识。
fn subscription_id(subscription: &Value) -> Result<&str> {
    let id = subscription
        .get("Id")
        .and_then(Value::as_str)
        .context("subscription is missing Id")?;
    entity_id_validate(id, "subscription")?;
    Ok(id)
}

/// 用途：校验订阅实体并补齐兼容所需的默认字段。
fn subscription_normalize(subscription: Value) -> Result<Value> {
    let mut subscription = subscription
        .as_object()
        .cloned()
        .context("subscription must be an object")?;
    let id = subscription
        .get("Id")
        .and_then(Value::as_str)
        .context("subscription is missing Id")?;
    entity_id_validate(id, "subscription")?;
    if subscription
        .get("AutoTestDelayIntervalMinutes")
        .is_none_or(Value::is_null)
    {
        let interval = if subscription
            .get("IsAutoTestDelayEnabled")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        {
            1
        } else {
            0
        };
        subscription.insert(
            "AutoTestDelayIntervalMinutes".into(),
            Value::from(interval),
        );
    }
    for name in [
        "OverrideIds",
        "OverrideSortPreference",
        "BuiltinChainProxyNames",
        "DisabledBuiltinChainProxyNames",
        "CustomChainProxies",
    ] {
        if subscription.get(name).is_none_or(Value::is_null) {
            subscription.insert(name.into(), Value::Array(Vec::new()));
        }
    }
    Ok(Value::Object(subscription))
}
