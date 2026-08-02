use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use serde_json::Value;

use crate::storage::{entity_id_validate, file_write_atomic, json_read_or_default};

/// 用途：管理用户覆盖配置的列表、内容文件及排序。
pub struct OverrideStore {
    directory: PathBuf,
    list_path: PathBuf,
}

/// 用途：提供覆盖配置实体、内容和排序的一致持久化接口。
impl OverrideStore {
    /// 用途：根据用户数据目录创建覆盖配置存储。
    pub fn new(user_data_dir: &Path) -> Self {
        Self {
            directory: user_data_dir.join("overrides"),
            list_path: user_data_dir.join("overrides").join("overrides_list.json"),
        }
    }

    /// 用途：读取并校验全部覆盖配置实体。
    pub fn list(&self) -> Result<Vec<Value>> {
        let document = json_read_or_default(
            &self.list_path,
            serde_json::json!({ "Overrides": [] }),
            "override",
        )?;
        document
            .get("Overrides")
            .and_then(Value::as_array)
            .context("override list must contain an Overrides array")?
            .iter()
            .cloned()
            .map(profile_validate)
            .collect()
    }

    /// 用途：新增或替换覆盖配置实体及其内容文件。
    pub fn save(&self, profile: Value, content: &str) -> Result<()> {
        let profile = profile_validate(profile)?;
        let id = profile_id(&profile)?.to_owned();
        let new_path = self.content_path_for_profile(&profile)?;
        let mut profiles = self.list()?;
        let index = profiles
            .iter()
            .position(|candidate| profile_id(candidate).ok() == Some(id.as_str()));
        let old_path = index
            .and_then(|index| self.content_path_for_profile(&profiles[index]).ok());
        file_write_atomic(&new_path, content.as_bytes())?;
        if let Some(index) = index {
            profiles[index] = profile;
        } else {
            profiles.push(profile);
        }
        self.write_list(&profiles)?;
        if let Some(old_path) = old_path
            && old_path != new_path
        {
            file_remove_if_exists(&old_path)?;
        }
        Ok(())
    }

    /// 用途：按调用方给出的完整列表保存覆盖配置顺序。
    pub fn reorder(&self, profiles: &[Value]) -> Result<()> {
        let profiles = profiles
            .iter()
            .cloned()
            .map(profile_validate)
            .collect::<Result<Vec<_>>>()?;
        self.write_list(&profiles)
    }

    /// 用途：读取指定覆盖配置的文本内容。
    pub fn read_content(&self, id: &str) -> Result<String> {
        let profile = self.find(id)?;
        let path = self.content_path_for_profile(&profile)?;
        std::fs::read_to_string(&path)
            .with_context(|| format!("failed to read override content: {}", path.display()))
    }

    /// 用途：返回指定覆盖配置的内容文件路径。
    pub fn content_path(&self, id: &str) -> Result<PathBuf> {
        let profile = self.find(id)?;
        self.content_path_for_profile(&profile)
    }

    /// 用途：删除覆盖配置实体及其内容文件。
    pub fn delete(&self, id: &str) -> Result<()> {
        entity_id_validate(id, "override")?;
        let mut profiles = self.list()?;
        let Some(index) = profiles
            .iter()
            .position(|candidate| profile_id(candidate).ok() == Some(id))
        else {
            return Ok(());
        };
        let profile = profiles.remove(index);
        self.write_list(&profiles)?;
        file_remove_if_exists(&self.content_path_for_profile(&profile)?)
    }

    /// 用途：按标识查找并返回覆盖配置实体。
    fn find(&self, id: &str) -> Result<Value> {
        entity_id_validate(id, "override")?;
        self.list()?
            .into_iter()
            .find(|profile| profile_id(profile).ok() == Some(id))
            .with_context(|| format!("override not found: {id}"))
    }

    /// 用途：根据覆盖配置格式计算对应内容文件路径。
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

    /// 用途：原子保存覆盖配置列表文档。
    fn write_list(&self, profiles: &[Value]) -> Result<()> {
        file_write_atomic(
            &self.list_path,
            &serde_json::to_vec_pretty(&serde_json::json!({ "Overrides": profiles }))?,
        )
    }
}

/// 用途：校验覆盖配置实体的结构和标识。
fn profile_validate(profile: Value) -> Result<Value> {
    if !profile.is_object() {
        bail!("override must be an object");
    }
    let _ = profile_id(&profile)?;
    Ok(profile)
}

/// 用途：提取并校验覆盖配置实体标识。
fn profile_id(profile: &Value) -> Result<&str> {
    let id = profile
        .get("Id")
        .and_then(Value::as_str)
        .context("override is missing Id")?;
    entity_id_validate(id, "override")?;
    Ok(id)
}

/// 用途：删除文件，并将文件不存在视为成功。
fn file_remove_if_exists(path: &Path) -> Result<()> {
    match std::fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => {
            Err(error).with_context(|| format!("failed to delete override: {}", path.display()))
        }
    }
}
