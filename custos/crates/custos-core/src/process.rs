//! Finds the Foxhole process. Under Proton it is a Wine process, so Custos looks for the name of the
//! Windows executable in the command lines of `/proc`.

use std::path::Path;

/// The executable of the Foxhole client.
pub const EXECUTABLE: &str = "War-Win64-Shipping.exe";

/// True when a process under `proc_root` (normally `/proc`) runs [`EXECUTABLE`].
#[must_use]
pub fn is_foxhole_running(proc_root: &Path) -> bool {
    let Ok(entries) = std::fs::read_dir(proc_root) else {
        return false;
    };

    entries
        .flatten()
        .filter(|entry| entry.file_name().to_string_lossy().bytes().all(|b| b.is_ascii_digit()))
        .filter_map(|entry| std::fs::read(entry.path().join("cmdline")).ok())
        .any(|cmdline| cmdline.split(|&b| b == 0).any(is_executable))
}

fn is_executable(arg: &[u8]) -> bool {
    let arg = String::from_utf8_lossy(arg);
    arg.rsplit(['/', '\\'])
        .next()
        .is_some_and(|name| name.eq_ignore_ascii_case(EXECUTABLE))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fake_proc(processes: &[(&str, &[u8])]) -> tempfile::TempDir {
        let root = tempfile::tempdir().unwrap();
        for (pid, cmdline) in processes {
            let dir = root.path().join(pid);
            std::fs::create_dir(&dir).unwrap();
            std::fs::write(dir.join("cmdline"), cmdline).unwrap();
        }
        std::fs::create_dir(root.path().join("self")).unwrap();
        root
    }

    #[test]
    fn wine_process_with_a_windows_path_is_found() {
        let root = fake_proc(&[
            ("10", b"/usr/bin/bash\0"),
            (
                "42",
                b"Z:\\steam\\steamapps\\common\\Foxhole\\War\\Binaries\\Win64\\War-Win64-Shipping.exe\0-log\0",
            ),
        ]);
        assert!(is_foxhole_running(root.path()));
    }

    #[test]
    fn unix_path_is_found() {
        let root = fake_proc(&[("7", b"/games/Foxhole/War/Binaries/Win64/war-win64-shipping.exe\0")]);
        assert!(is_foxhole_running(root.path()));
    }

    #[test]
    fn other_processes_are_not_foxhole() {
        let root = fake_proc(&[("10", b"/usr/bin/vim\0War-Win64-Shipping.exe.log\0"), ("11", b"")]);
        assert!(!is_foxhole_running(root.path()));
    }

    #[test]
    fn missing_root_is_not_running() {
        assert!(!is_foxhole_running(Path::new("/does/not/exist")));
    }
}
