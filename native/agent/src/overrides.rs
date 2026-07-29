use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use serde_json::Value;

pub struct OverrideStore {
    directory: PathBuf,
    list_path: PathBuf,
}

impl OverrideStore {
    pub fn new(user_data_dir: &Path) -> Self {
        Self {
            directory: user_data_dir.join("overrides"),
            list_path: user_data_dir.join("overrides").join("overrides_list.json"),
        }
    }

    pub fn list(&self) -> Result<Vec<Value>> {
        let document = read_json_or_default(
            &self.list_path,
            serde_json::json!({ "Overrides": [] }),
        )?;
        document
            .get("Overrides")
            .and_then(Value::as_array)
            .context("override list must contain an Overrides array")?
            .iter()
            .cloned()
            .map(validate_profile)
            .collect()
    }

    pub fn save(&self, profile: Value, content: &str) -> Result<()> {
        let profile = validate_profile(profile)?;
        let id = profile_id(&profile)?.to_owned();
        let new_path = self.content_path_for_profile(&profile)?;
        let mut profiles = self.list()?;
        let index = profiles
            .iter()
            .position(|candidate| profile_id(candidate).ok() == Some(id.as_str()));
        let old_path = index
            .and_then(|index| self.content_path_for_profile(&profiles[index]).ok());
        atomic_write(&new_path, content.as_bytes())?;
        if let Some(index) = index {
            profiles[index] = profile;
        } else {
            profiles.push(profile);
        }
        self.write_list(&profiles)?;
        if let Some(old_path) = old_path
            && old_path != new_path
        {
            remove_if_exists(&old_path)?;
        }
        Ok(())
    }

    pub fn reorder(&self, profiles: &[Value]) -> Result<()> {
        let profiles = profiles
            .iter()
            .cloned()
            .map(validate_profile)
            .collect::<Result<Vec<_>>>()?;
        self.write_list(&profiles)
    }

    pub fn read_content(&self, id: &str) -> Result<String> {
        let profile = self.find(id)?;
        let path = self.content_path_for_profile(&profile)?;
        std::fs::read_to_string(&path)
            .with_context(|| format!("failed to read override content: {}", path.display()))
    }

    pub fn content_path(&self, id: &str) -> Result<PathBuf> {
        let profile = self.find(id)?;
        self.content_path_for_profile(&profile)
    }

    pub fn delete(&self, id: &str) -> Result<()> {
        validate_id(id)?;
        let mut profiles = self.list()?;
        let Some(index) = profiles
            .iter()
            .position(|candidate| profile_id(candidate).ok() == Some(id))
        else {
            return Ok(());
        };
        let profile = profiles.remove(index);
        self.write_list(&profiles)?;
        remove_if_exists(&self.content_path_for_profile(&profile)?)
    }

    fn find(&self, id: &str) -> Result<Value> {
        validate_id(id)?;
        self.list()?
            .into_iter()
            .find(|profile| profile_id(profile).ok() == Some(id))
            .with_context(|| format!("override not found: {id}"))
    }

    fn content_path_for_profile(&self, profile: &Value) -> Result<PathBuf> {
        let id = profile_id(profile)?;
        let extension = match profile.get("Format") {
            Some(Value::String(value)) if value.eq_ignore_ascii_case("yaml") => "yaml",
            Some(Value::Number(value)) if value.as_i64() == Some(0) => "yaml",
            Some(Value::String(value)) if value.eq_ignore_ascii_case("javascript") => "js",
            Some(Value::String(value)) if value.eq_ignore_ascii_case("js") => "js",
            Some(Value::Number(value)) if value.as_i64() == Some(1) => "js",
            _ => bail!("override has an invalid Format"),
        };
        Ok(self.directory.join(format!("{id}.{extension}")))
    }

    fn write_list(&self, profiles: &[Value]) -> Result<()> {
        atomic_write(
            &self.list_path,
            &serde_json::to_vec_pretty(&serde_json::json!({ "Overrides": profiles }))?,
        )
    }
}

fn validate_profile(profile: Value) -> Result<Value> {
    if !profile.is_object() {
        bail!("override must be an object");
    }
    let _ = profile_id(&profile)?;
    Ok(profile)
}

fn profile_id(profile: &Value) -> Result<&str> {
    let id = profile
        .get("Id")
        .and_then(Value::as_str)
        .context("override is missing Id")?;
    validate_id(id)?;
    Ok(id)
}

fn validate_id(id: &str) -> Result<()> {
    if id.is_empty()
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
    {
        bail!("override id contains invalid characters");
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
            let backup = path.with_extension("json.corrupt");
            remove_if_exists(&backup)?;
            if let Err(backup_error) = std::fs::rename(path, &backup) {
                tracing::warn!(
                    "failed to back up corrupt override state {}: {backup_error}",
                    path.display()
                );
            }
            tracing::warn!("invalid override state {} was reset: {error}", path.display());
            Ok(default)
        }
    }
}

fn atomic_write(path: &Path, content: &[u8]) -> Result<()> {
    let parent = path.parent().context("override path has no parent")?;
    std::fs::create_dir_all(parent)
        .with_context(|| format!("failed to create override directory: {}", parent.display()))?;
    let temporary = path.with_extension("agent.tmp");
    std::fs::write(&temporary, content)
        .with_context(|| format!("failed to write temporary override: {}", temporary.display()))?;
    if let Err(error) = std::fs::rename(&temporary, path) {
        if path.exists() {
            std::fs::remove_file(path)
                .with_context(|| format!("failed to replace override: {}", path.display()))?;
            std::fs::rename(&temporary, path)
                .with_context(|| format!("failed to install override: {}", path.display()))?;
        } else {
            return Err(error)
                .with_context(|| format!("failed to install override: {}", path.display()));
        }
    }
    Ok(())
}

fn remove_if_exists(path: &Path) -> Result<()> {
    match std::fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => {
            Err(error).with_context(|| format!("failed to delete override: {}", path.display()))
        }
    }
}
