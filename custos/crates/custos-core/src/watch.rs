//! Watches one file for changes.

use std::path::Path;

use notify::{RecommendedWatcher, RecursiveMode, Watcher};
use tokio::sync::mpsc;

/// Sends `()` when the file changes. The folder of the file is watched, not the file, because a program
/// can replace the file with a new one, for example a game or an editor.
pub fn file(path: &Path, changed: mpsc::UnboundedSender<()>) -> notify::Result<RecommendedWatcher> {
    let name = path.file_name().map(ToOwned::to_owned);
    let mut watcher = notify::recommended_watcher(move |event: notify::Result<notify::Event>| {
        if let Ok(event) = event
            && !event.kind.is_access()
            && event.paths.iter().any(|p| p.file_name() == name.as_deref())
        {
            let _ = changed.send(());
        }
    })?;
    watcher.watch(path.parent().unwrap_or(path), RecursiveMode::NonRecursive)?;
    Ok(watcher)
}
