use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};

pub struct RuntimeFileStore {
    runtime_dir: PathBuf,
    core_dir: PathBuf,
}

impl RuntimeFileStore {
    pub fn new(user_data_dir: &Path, core_dir: &Path) -> Self {
        Self {
            runtime_dir: user_data_dir.join("runtime"),
            core_dir: core_dir.to_owned(),
        }
    }

    pub fn save(
        &self,
        subscription_id: &str,
        original: &str,
        runtime: &str,
    ) -> Result<(PathBuf, PathBuf)> {
        validate_id(subscription_id)?;
        let directory = self.runtime_dir.join(subscription_id);
        let original_path = directory.join("original.yaml");
        let runtime_path = directory.join("runtime.yaml");
        atomic_write(&original_path, original.as_bytes())?;
        atomic_write(&runtime_path, runtime.as_bytes())?;
        Ok((original_path, runtime_path))
    }

    pub fn save_empty(&self, runtime: &str) -> Result<PathBuf> {
        let path = self.runtime_dir.join("empty").join("runtime.yaml");
        atomic_write(&path, runtime.as_bytes())?;
        Ok(path)
    }

    pub fn read(&self, subscription_id: &str) -> Result<String> {
        validate_id(subscription_id)?;
        let path = self.runtime_dir.join(subscription_id).join("runtime.yaml");
        std::fs::read_to_string(&path)
            .with_context(|| format!("failed to read runtime config: {}", path.display()))
    }

    pub fn delete(&self, subscription_id: &str) -> Result<()> {
        validate_id(subscription_id)?;
        let path = self.runtime_dir.join(subscription_id);
        match std::fs::remove_dir_all(&path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error)
                .with_context(|| format!("failed to delete runtime config: {}", path.display())),
        }
    }

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

fn validate_id(id: &str) -> Result<()> {
    if id.is_empty()
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
    {
        bail!("runtime id contains invalid characters");
    }
    Ok(())
}

fn atomic_write(path: &Path, content: &[u8]) -> Result<()> {
    let parent = path.parent().context("runtime path has no parent")?;
    std::fs::create_dir_all(parent)
        .with_context(|| format!("failed to create runtime directory: {}", parent.display()))?;
    let temporary = path.with_extension("agent.tmp");
    std::fs::write(&temporary, content)
        .with_context(|| format!("failed to write temporary runtime: {}", temporary.display()))?;
    if let Err(error) = std::fs::rename(&temporary, path) {
        if path.exists() {
            std::fs::remove_file(path)
                .with_context(|| format!("failed to replace runtime: {}", path.display()))?;
            std::fs::rename(&temporary, path)
                .with_context(|| format!("failed to install runtime: {}", path.display()))?;
        } else {
            return Err(error)
                .with_context(|| format!("failed to install runtime: {}", path.display()));
        }
    }
    Ok(())
}
