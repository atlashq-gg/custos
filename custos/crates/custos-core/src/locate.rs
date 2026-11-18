//! Finds the map data file of Foxhole in the Proton prefix of Steam.
//!
//! The save folder holds `UserData.sav`, which names the Steam ID of the player, and `<id>_MapData.sav`.
//! The Steam ID is used only to build the file name. It never leaves the computer.

use std::path::{Path, PathBuf};

/// The Steam app ID of Foxhole.
pub const APP_ID: &str = "505460";

/// The save folder inside a Steam library.
const SAVE_DIR_IN_LIBRARY: &str =
    "steamapps/compatdata/505460/pfx/drive_c/users/steamuser/AppData/Local/Foxhole/Saved/SaveGames";

const USER_DATA: &str = "UserData.sav";
const MAP_DATA_SUFFIX: &str = "_MapData.sav";

#[derive(Debug, thiserror::Error)]
pub enum LocateError {
    #[error("cannot find the Foxhole save folder in a Steam library. Set save_dir in the configuration.")]
    NoSaveDir,
    #[error("cannot find a map data file in {0}. Start Foxhole one time, or set save_dir.")]
    NoMapData(PathBuf),
}

/// The usual Steam folders on Linux, in order: the native client, then the Flatpak client.
#[must_use]
pub fn default_steam_roots(home: &Path) -> Vec<PathBuf> {
    [
        ".steam/steam",
        ".local/share/Steam",
        ".var/app/com.valvesoftware.Steam/.local/share/Steam",
    ]
    .iter()
    .map(|relative| home.join(relative))
    .collect()
}

/// Finds the save folder. `save_dir` from the configuration wins. Else each Steam root and each of its
/// libraries is searched.
pub fn find_save_dir(save_dir: Option<&Path>, steam_roots: &[PathBuf]) -> Result<PathBuf, LocateError> {
    if let Some(dir) = save_dir {
        return if dir.is_dir() {
            Ok(dir.to_owned())
        } else {
            Err(LocateError::NoSaveDir)
        };
    }

    steam_roots
        .iter()
        .flat_map(|root| {
            let vdf = std::fs::read_to_string(root.join("steamapps/libraryfolders.vdf")).unwrap_or_default();
            std::iter::once(root.clone()).chain(library_paths(&vdf))
        })
        .map(|library| library.join(SAVE_DIR_IN_LIBRARY))
        .find(|dir| dir.is_dir())
        .ok_or(LocateError::NoSaveDir)
}

/// The map data file in the save folder.
pub fn find_map_data(save_dir: &Path) -> Result<PathBuf, LocateError> {
    if let Some(id) = std::fs::read(save_dir.join(USER_DATA))
        .ok()
        .and_then(|bytes| steam_id(&bytes))
    {
        let file = save_dir.join(format!("{id}{MAP_DATA_SUFFIX}"));
        if file.is_file() {
            return Ok(file);
        }
    }

    // No usable UserData.sav: use the only map data file, if there is exactly one.
    let mut files = std::fs::read_dir(save_dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|entry| entry.path())
        .filter(|path| {
            path.file_name()
                .is_some_and(|n| n.to_string_lossy().ends_with(MAP_DATA_SUFFIX))
        });
    match (files.next(), files.next()) {
        (Some(file), None) => Ok(file),
        _ => Err(LocateError::NoMapData(save_dir.to_owned())),
    }
}

/// Reads the Steam ID from `UserData.sav`: a little-endian `i32` length, then that many bytes with a
/// final NUL. Only digits are accepted, because the ID becomes part of a path.
fn steam_id(bytes: &[u8]) -> Option<String> {
    let length = usize::try_from(i32::from_le_bytes(bytes.get(..4)?.try_into().ok()?)).ok()?;
    let text = bytes.get(4..4 + length)?.strip_suffix(&[0])?;
    let id = std::str::from_utf8(text).ok()?;
    (!id.is_empty() && id.bytes().all(|b| b.is_ascii_digit())).then(|| id.to_owned())
}

/// The library paths in `libraryfolders.vdf`: the value after each `"path"` key.
fn library_paths(vdf: &str) -> Vec<PathBuf> {
    let tokens = quoted_tokens(vdf);
    tokens
        .windows(2)
        .filter(|pair| pair[0] == "path")
        .map(|pair| PathBuf::from(&pair[1]))
        .collect()
}

fn quoted_tokens(vdf: &str) -> Vec<String> {
    let mut tokens = Vec::new();
    let mut chars = vdf.chars();
    while let Some(c) = chars.next() {
        if c != '"' {
            continue;
        }
        let mut token = String::new();
        while let Some(c) = chars.next() {
            match c {
                '"' => break,
                '\\' => token.extend(chars.next()),
                c => token.push(c),
            }
        }
        tokens.push(token);
    }
    tokens
}

#[cfg(test)]
mod tests {
    use super::*;

    fn user_data(id: &str) -> Vec<u8> {
        let length = i32::try_from(id.len() + 1).unwrap();
        [&length.to_le_bytes()[..], id.as_bytes(), &[0]].concat()
    }

    #[test]
    fn steam_id_is_read_from_user_data() {
        assert_eq!(
            steam_id(&user_data("76561190000000000")).as_deref(),
            Some("76561190000000000")
        );
    }

    #[test]
    fn user_data_that_is_not_a_number_is_refused() {
        assert_eq!(steam_id(&user_data("../../etc")), None);
        assert_eq!(steam_id(&[1, 0]), None);
    }

    #[test]
    fn library_paths_are_read_from_the_vdf() {
        let vdf = r#"
        "libraryfolders"
        {
            "0" { "path" "/home/u/.local/share/Steam" "apps" { "228980" "1" } }
            "1" { "path" "/mnt/games/Steam\\Library" "apps" { "505460" "2" } }
        }"#;
        assert_eq!(
            library_paths(vdf),
            [
                PathBuf::from("/home/u/.local/share/Steam"),
                PathBuf::from("/mnt/games/Steam\\Library")
            ]
        );
    }

    #[test]
    fn save_dir_is_found_in_a_second_library() {
        let home = tempfile::tempdir().unwrap();
        let root = home.path().join("steam");
        let library = home.path().join("games");
        std::fs::create_dir_all(root.join("steamapps")).unwrap();
        std::fs::write(
            root.join("steamapps/libraryfolders.vdf"),
            format!(r#""libraryfolders" {{ "1" {{ "path" "{}" }} }}"#, library.display()),
        )
        .unwrap();
        std::fs::create_dir_all(library.join(SAVE_DIR_IN_LIBRARY)).unwrap();

        let dir = find_save_dir(None, &[home.path().join("missing"), root]).unwrap();

        assert_eq!(dir, library.join(SAVE_DIR_IN_LIBRARY));
    }

    #[test]
    fn configured_save_dir_wins() {
        let dir = tempfile::tempdir().unwrap();
        assert_eq!(find_save_dir(Some(dir.path()), &[]).unwrap(), dir.path());
        assert!(find_save_dir(Some(&dir.path().join("x")), &[]).is_err());
    }

    #[test]
    fn map_data_is_found_through_user_data() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join(USER_DATA), user_data("123")).unwrap();
        std::fs::write(dir.path().join("123_MapData.sav"), b"GVAS").unwrap();
        std::fs::write(dir.path().join("456_MapData.sav"), b"GVAS").unwrap();

        assert_eq!(find_map_data(dir.path()).unwrap(), dir.path().join("123_MapData.sav"));
    }

    #[test]
    fn single_map_data_file_is_used_without_user_data() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("123_MapData.sav"), b"GVAS").unwrap();
        assert_eq!(find_map_data(dir.path()).unwrap(), dir.path().join("123_MapData.sav"));

        std::fs::write(dir.path().join("456_MapData.sav"), b"GVAS").unwrap();
        assert!(find_map_data(dir.path()).is_err());
    }
}
