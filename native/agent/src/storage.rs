use std::path::Path;

use anyhow::{Context, Result, bail};
use serde_json::Value;

/// 用途：将内容写入临时文件后原子替换目标文件。
pub fn file_write_atomic(path: &Path, content: &[u8]) -> Result<()> {
    let parent = path.parent().context("storage path has no parent")?;
    std::fs::create_dir_all(parent)
        .with_context(|| format!("failed to create storage directory: {}", parent.display()))?;
    let temporary = path.with_extension(
        path.extension()
            .and_then(|extension| extension.to_str())
            .map(|extension| format!("{extension}.agent.tmp"))
            .unwrap_or_else(|| "agent.tmp".into()),
    );
    std::fs::write(&temporary, content)
        .with_context(|| format!("failed to write temporary file: {}", temporary.display()))?;
    if let Err(error) = std::fs::rename(&temporary, path) {
        if path.exists() {
            std::fs::remove_file(path)
                .with_context(|| format!("failed to replace file: {}", path.display()))?;
            std::fs::rename(&temporary, path)
                .with_context(|| format!("failed to install file: {}", path.display()))?;
        } else {
            return Err(error)
                .with_context(|| format!("failed to install file: {}", path.display()));
        }
    }
    Ok(())
}

/// 用途：校验可安全用于文件名的实体标识符。
pub fn entity_id_validate(id: &str, kind: &str) -> Result<()> {
    if id.is_empty()
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
    {
        bail!("{kind} id contains invalid characters");
    }
    Ok(())
}

/// 用途：读取 JSON 状态；文件不存在或内容损坏时返回默认值。
pub fn json_read_or_default(path: &Path, default: Value, kind: &str) -> Result<Value> {
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
                    "failed to remove previous corrupt {kind} backup {}: {backup_error}",
                    backup.display()
                );
            }
            if let Err(backup_error) = std::fs::rename(path, &backup) {
                tracing::warn!(
                    "failed to back up corrupt {kind} state {}: {backup_error}",
                    path.display()
                );
            }
            tracing::warn!("invalid {kind} state {} was reset: {error}", path.display());
            Ok(default)
        }
    }
}
