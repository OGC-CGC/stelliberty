use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};

use crate::storage::{entity_id_validate, file_write_atomic};

/// 用途：管理订阅生成的运行时配置文件及 Provider 文件。
pub struct RuntimeFileStore {
    runtime_dir: PathBuf,
    core_dir: PathBuf,
}

/// 用途：提供运行时配置和 Provider 文件的一致访问接口。
impl RuntimeFileStore {
    /// 用途：根据用户数据目录和核心目录创建运行时文件存储。
    pub fn new(user_data_dir: &Path, core_dir: &Path) -> Self {
        Self {
            runtime_dir: user_data_dir.join("runtime"),
            core_dir: core_dir.to_owned(),
        }
    }

    /// 用途：保存订阅原始配置和生成后的运行时配置。
    pub fn save(
        &self,
        subscription_id: &str,
        original: &str,
        runtime: &str,
    ) -> Result<(PathBuf, PathBuf)> {
        entity_id_validate(subscription_id, "runtime")?;
        let directory = self.runtime_dir.join(subscription_id);
        let original_path = directory.join("original.yaml");
        let runtime_path = directory.join("runtime.yaml");
        file_write_atomic(&original_path, original.as_bytes())?;
        file_write_atomic(&runtime_path, runtime.as_bytes())?;
        Ok((original_path, runtime_path))
    }

    /// 用途：保存无订阅场景使用的空运行时配置。
    pub fn save_empty(&self, runtime: &str) -> Result<PathBuf> {
        let path = self.runtime_dir.join("empty").join("runtime.yaml");
        file_write_atomic(&path, runtime.as_bytes())?;
        Ok(path)
    }

    /// 用途：读取指定订阅最后生成的运行时配置。
    pub fn read(&self, subscription_id: &str) -> Result<String> {
        entity_id_validate(subscription_id, "runtime")?;
        let path = self.runtime_dir.join(subscription_id).join("runtime.yaml");
        std::fs::read_to_string(&path)
            .with_context(|| format!("failed to read runtime config: {}", path.display()))
    }

    /// 用途：删除指定订阅的全部运行时配置文件。
    pub fn delete(&self, subscription_id: &str) -> Result<()> {
        entity_id_validate(subscription_id, "runtime")?;
        let path = self.runtime_dir.join(subscription_id);
        match std::fs::remove_dir_all(&path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error)
                .with_context(|| format!("failed to delete runtime config: {}", path.display())),
        }
    }

    /// 用途：在核心目录边界内解析并读取 Provider 文件。
    pub fn read_provider(&self, path: &str) -> Result<String> {
        let requested = PathBuf::from(path);
        let requested = if requested.is_absolute() {
            requested
        } else {
            self.core_dir.join(path.strip_prefix("./").unwrap_or(path))
        };
        let root = std::fs::canonicalize(&self.core_dir)
            .with_context(|| format!("failed to resolve core directory: {}", self.core_dir.display()))?;
        let requested = std::fs::canonicalize(&requested)
            .with_context(|| format!("failed to resolve provider config: {}", requested.display()))?;
        if !requested.starts_with(&root) {
            bail!("provider config is outside the core directory");
        }
        std::fs::read_to_string(&requested)
            .with_context(|| format!("failed to read provider config: {}", requested.display()))
    }
}
