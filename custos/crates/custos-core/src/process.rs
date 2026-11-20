//! Finds the Foxhole process.
//!
//! On Linux, Foxhole runs in Proton as a Wine process. Custos looks for the name of the Windows
//! executable in the command lines of `/proc`. On Windows, Custos compares the image name of each
//! process. It does not read the memory of a process.

#[cfg(target_os = "linux")]
use std::path::Path;

/// The executable of the Foxhole client.
pub const EXECUTABLE: &str = "War-Win64-Shipping.exe";

/// True when a process runs [`EXECUTABLE`].
#[cfg(target_os = "linux")]
#[must_use]
pub fn is_foxhole_running() -> bool {
    runs_in(Path::new("/proc"))
}

/// True when a process runs [`EXECUTABLE`].
#[cfg(windows)]
#[must_use]
pub fn is_foxhole_running() -> bool {
    use sysinfo::{ProcessRefreshKind, ProcessesToUpdate, System};

    // Only the snapshot of the process list: no command line and no other data of the process.
    let mut system = System::new();
    system.refresh_processes_specifics(ProcessesToUpdate::All, true, ProcessRefreshKind::nothing());
    system
        .processes()
        .values()
        .any(|p| is_executable(p.name().as_encoded_bytes()))
}

/// True when a process under `proc_root` runs [`EXECUTABLE`].
#[cfg(target_os = "linux")]
fn runs_in(proc_root: &Path) -> bool {
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

#[cfg(all(test, target_os = "linux"))]
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
        assert!(runs_in(root.path()));
    }

    #[test]
    fn unix_path_is_found() {
        let root = fake_proc(&[("7", b"/games/Foxhole/War/Binaries/Win64/war-win64-shipping.exe\0")]);
        assert!(runs_in(root.path()));
    }

    #[test]
    fn other_processes_are_not_foxhole() {
        let root = fake_proc(&[("10", b"/usr/bin/vim\0War-Win64-Shipping.exe.log\0"), ("11", b"")]);
        assert!(!runs_in(root.path()));
    }

    #[test]
    fn missing_root_is_not_running() {
        assert!(!runs_in(Path::new("/does/not/exist")));
    }
}
