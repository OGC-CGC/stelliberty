use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use serde_json::{Map, Value};

pub struct SubscriptionStore {
    subscriptions_dir: PathBuf,
    list_path: PathBuf,
    subscription_selection_path: PathBuf,
    proxy_selection_path: PathBuf,
}

impl SubscriptionStore {
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

    pub fn list(&self) -> Result<Vec<Value>> {
        let document = read_json_or_default(
            &self.list_path,
            serde_json::json!({ "Subscriptions": [] }),
        )?;
        let subscriptions = document
            .get("Subscriptions")
            .and_then(Value::as_array)
            .context("subscription list must contain a Subscriptions array")?;
        subscriptions
            .iter()
            .cloned()
            .map(normalize_subscription)
            .collect()
    }

    pub fn save(&self, subscription: Value, content: &str) -> Result<()> {
        let subscription = normalize_subscription(subscription)?;
        let id = subscription_id(&subscription)?.to_owned();
        atomic_write(&self.content_path(&id)?, content.as_bytes())?;
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

    pub fn update(&self, subscription: Value) -> Result<()> {
        let subscription = normalize_subscription(subscription)?;
        let id = subscription_id(&subscription)?.to_owned();
        let mut subscriptions = self.list()?;
        let index = subscriptions
            .iter()
            .position(|candidate| subscription_id(candidate).ok() == Some(id.as_str()))
            .with_context(|| format!("subscription not found: {id}"))?;
        subscriptions[index] = subscription;
        self.write_list(&subscriptions)
    }

    pub fn reorder(&self, subscriptions: &[Value]) -> Result<()> {
        let subscriptions = subscriptions
            .iter()
            .cloned()
            .map(normalize_subscription)
            .collect::<Result<Vec<_>>>()?;
        self.write_list(&subscriptions)
    }

    pub fn read_content(&self, subscription_id: &str) -> Result<String> {
        let path = self.content_path(subscription_id)?;
        std::fs::read_to_string(&path)
            .with_context(|| format!("failed to read subscription content: {}", path.display()))
    }

    pub fn save_content(&self, subscription_id: &str, content: &str) -> Result<()> {
        let path = self.content_path(subscription_id)?;
        atomic_write(&path, content.as_bytes())
    }

    pub fn content_path(&self, subscription_id: &str) -> Result<PathBuf> {
        validate_id(subscription_id)?;
        Ok(self
            .subscriptions_dir
            .join(format!("{subscription_id}.yaml")))
    }

    pub fn delete(&self, id: &str) -> Result<()> {
        validate_id(id)?;
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

    pub fn current_subscription_id(&self) -> Result<Option<String>> {
        let document = read_json_or_default(
            &self.subscription_selection_path,
            serde_json::json!({ "CurrentSubscriptionId": null }),
        )?;
        Ok(document
            .get("CurrentSubscriptionId")
            .and_then(Value::as_str)
            .map(str::to_owned))
    }

    pub fn set_current_subscription_id(&self, subscription_id: Option<&str>) -> Result<()> {
        if let Some(subscription_id) = subscription_id {
            validate_id(subscription_id)?;
        }
        atomic_write(
            &self.subscription_selection_path,
            &serde_json::to_vec_pretty(&serde_json::json!({
                "CurrentSubscriptionId": subscription_id,
            }))?,
        )
    }

    pub fn proxy_selections(&self, subscription_id: &str) -> Result<Map<String, Value>> {
        validate_id(subscription_id)?;
        let document = self.read_proxy_selection_document()?;
        Ok(document
            .get("Subscriptions")
            .and_then(Value::as_object)
            .and_then(|subscriptions| subscriptions.get(subscription_id))
            .and_then(Value::as_object)
            .cloned()
            .unwrap_or_default())
    }

    pub fn set_proxy_selection(
        &self,
        subscription_id: &str,
        group_name: &str,
        proxy_name: &str,
    ) -> Result<()> {
        validate_id(subscription_id)?;
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

    pub fn remove_proxy_selection(
        &self,
        subscription_id: &str,
        group_name: &str,
    ) -> Result<()> {
        validate_id(subscription_id)?;
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

    fn write_list(&self, subscriptions: &[Value]) -> Result<()> {
        atomic_write(
            &self.list_path,
            &serde_json::to_vec_pretty(&serde_json::json!({
                "Subscriptions": subscriptions,
            }))?,
        )
    }

    fn read_proxy_selection_document(&self) -> Result<Map<String, Value>> {
        read_json_or_default(
            &self.proxy_selection_path,
            serde_json::json!({ "Subscriptions": {} }),
        )?
        .as_object()
        .cloned()
        .context("proxy selection state must be an object")
    }

    fn write_proxy_selection_document(&self, document: &Map<String, Value>) -> Result<()> {
        atomic_write(
            &self.proxy_selection_path,
            &serde_json::to_vec_pretty(document)?,
        )
    }
}

fn subscription_id(subscription: &Value) -> Result<&str> {
    let id = subscription
        .get("Id")
        .and_then(Value::as_str)
        .context("subscription is missing Id")?;
    validate_id(id)?;
    Ok(id)
}

fn normalize_subscription(subscription: Value) -> Result<Value> {
    let mut subscription = subscription
        .as_object()
        .cloned()
        .context("subscription must be an object")?;
    let id = subscription
        .get("Id")
        .and_then(Value::as_str)
        .context("subscription is missing Id")?;
    validate_id(id)?;
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

fn validate_id(id: &str) -> Result<()> {
    if id.is_empty()
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
    {
        bail!("subscription id contains invalid characters");
    }
    Ok(())
}

fn read_json_or_default(path: &Path, default: Value) -> Result<Value> {
    let content = match std::fs::read_to_string(path) {
        Ok(content) => content,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(default),
        Err(error) => {
            return Err(error).with_context(|| format!("failed to read {}", path.display()));
        }
    };
    match serde_json::from_str(&content) {
        Ok(document) => Ok(document),
        Err(error) => {
            let backup = path.with_extension(
                path.extension()
                    .and_then(|extension| extension.to_str())
                    .map(|extension| format!("{extension}.corrupt"))
                    .unwrap_or_else(|| "corrupt".into()),
            );
            if backup.is_file()
                && let Err(backup_error) = std::fs::remove_file(&backup)
            {
                tracing::warn!(
                    "failed to remove previous corrupt state backup {}: {backup_error}",
                    backup.display()
                );
            }
            if let Err(backup_error) = std::fs::rename(path, &backup) {
                tracing::warn!(
                    "failed to back up corrupt state {}: {backup_error}",
                    path.display()
                );
            }
            tracing::warn!("invalid JSON state {} was reset: {error}", path.display());
            Ok(default)
        }
    }
}

fn atomic_write(path: &Path, content: &[u8]) -> Result<()> {
    let parent = path.parent().context("state path has no parent")?;
    std::fs::create_dir_all(parent)
        .with_context(|| format!("failed to create state directory: {}", parent.display()))?;
    let temporary = path.with_extension(
        path.extension()
            .and_then(|extension| extension.to_str())
            .map(|extension| format!("{extension}.agent.tmp"))
            .unwrap_or_else(|| "agent.tmp".into()),
    );
    std::fs::write(&temporary, content)
        .with_context(|| format!("failed to write temporary state: {}", temporary.display()))?;
    if let Err(error) = std::fs::rename(&temporary, path) {
        if path.exists() {
            std::fs::remove_file(path)
                .with_context(|| format!("failed to replace state: {}", path.display()))?;
            std::fs::rename(&temporary, path)
                .with_context(|| format!("failed to install state: {}", path.display()))?;
        } else {
            return Err(error)
                .with_context(|| format!("failed to install state: {}", path.display()));
        }
    }
    Ok(())
}
