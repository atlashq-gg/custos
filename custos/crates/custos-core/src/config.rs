//! The configuration file: `$XDG_CONFIG_HOME/custos/config.toml` on Linux, and
//! `%APPDATA%\custos\config.toml` on Windows.

use std::path::{Path, PathBuf};

use serde::Deserialize;
use url::Url;

/// The interval between two uploads when the file does not set one.
pub const DEFAULT_UPLOAD_INTERVAL_SECONDS: u64 = 300;

// The lines of the template that depend on the platform. A path in single quotes is a literal
// string, so the backslashes of Windows need no escape.
#[cfg(not(windows))]
macro_rules! platform_template {
    () => {
        r#"
# The folder that holds the save files of Foxhole. Custos finds it in the Proton prefix when this is not set.
# save_dir = "/home/user/.local/share/Steam/steamapps/compatdata/505460/pfx/drive_c/users/steamuser/AppData/Local/Foxhole/Saved/SaveGames"

# The Steam folder. Custos tries the usual locations when this is not set.
# steam_root = "/home/user/.local/share/Steam"
"#
    };
}
#[cfg(windows)]
macro_rules! platform_template {
    () => {
        r#"
# The folder that holds the save files of Foxhole. Default: %LOCALAPPDATA%\Foxhole\Saved\SaveGames.
# save_dir = 'C:\Users\user\AppData\Local\Foxhole\Saved\SaveGames'
"#
    };
}

/// The content of a new configuration file. Custos writes it when no file exists.
pub const TEMPLATE: &str = concat!(
    r#"# Custos configuration.

# The URL of the service. Custos gets all other settings from its discovery document.
# service_url = "https://example.com"

# The shortest time between two uploads, in seconds. The service can set a longer time.
# upload_interval_seconds = 300
"#,
    platform_template!(),
    r#"
# The overlay shows the messages above the game while Foxhole runs. Without it, Custos shows
# desktop notifications.
# [overlay]
# enabled = true
# The corner of the screen: "top-left", "top-right", "bottom-left", or "bottom-right".
# anchor = "top-left"
# The distance from the top or bottom edge, in percent of the monitor height, from 0 to 90. In the
# top left corner, the default puts the overlay below the notifications of the game.
# offset = 28
"#
);

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Config {
    pub service_url: Url,
    #[serde(default = "default_interval")]
    pub upload_interval_seconds: u64,
    pub save_dir: Option<PathBuf>,
    /// Linux only.
    pub steam_root: Option<PathBuf>,
    #[serde(default)]
    pub overlay: Overlay,
}

/// The `[overlay]` table.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Overlay {
    pub enabled: bool,
    pub anchor: Anchor,
    /// The distance from the top or bottom edge, in percent of the monitor height.
    pub offset: u8,
}

/// The largest value of [`Overlay::offset`].
pub const MAX_OVERLAY_OFFSET: u8 = 90;

impl Default for Overlay {
    fn default() -> Self {
        Self {
            enabled: true,
            anchor: Anchor::TopLeft,
            offset: 28,
        }
    }
}

/// The corner of the screen where the overlay shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Anchor {
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

impl Anchor {
    #[must_use]
    pub fn is_top(self) -> bool {
        matches!(self, Anchor::TopLeft | Anchor::TopRight)
    }

    #[must_use]
    pub fn is_left(self) -> bool {
        matches!(self, Anchor::TopLeft | Anchor::BottomLeft)
    }
}

fn default_interval() -> u64 {
    DEFAULT_UPLOAD_INTERVAL_SECONDS
}

#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("no configuration file at {0}. Custos created a template: set service_url in it.")]
    Created(PathBuf),
    #[error("the configuration file {path} has no service_url. Set it, then restart Custos.")]
    Incomplete { path: PathBuf },
    #[error("cannot read {path}: {source}")]
    Io { path: PathBuf, source: std::io::Error },
    #[error("the configuration file {path} is not valid: {source}")]
    Parse { path: PathBuf, source: toml::de::Error },
    #[error("the service URL must use HTTPS, or HTTP to localhost: {0}")]
    Insecure(Url),
    #[error("the service URL must not have a query or a fragment: {0}")]
    QueryOrFragment(Url),
    #[error("overlay.offset must be at most {MAX_OVERLAY_OFFSET}: {0}")]
    OverlayOffset(u8),
    #[error("cannot find the configuration folder of this user")]
    NoConfigDir,
}

/// The path of the configuration file.
pub fn default_path() -> Result<PathBuf, ConfigError> {
    dirs::config_dir()
        .map(|dir| dir.join("custos").join("config.toml"))
        .ok_or(ConfigError::NoConfigDir)
}

/// Reads the configuration. If the file does not exist, writes [`TEMPLATE`] and returns
/// [`ConfigError::Created`].
pub fn load(path: &Path) -> Result<Config, ConfigError> {
    let text = match std::fs::read_to_string(path) {
        Ok(text) => text,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            write_template(path).map_err(|source| ConfigError::Io {
                path: path.into(),
                source,
            })?;
            return Err(ConfigError::Created(path.into()));
        }
        Err(source) => {
            return Err(ConfigError::Io {
                path: path.into(),
                source,
            });
        }
    };

    parse(&text).map_err(|e| match e {
        ParseError::Toml(source) if source.message().contains("service_url") => {
            ConfigError::Incomplete { path: path.into() }
        }
        ParseError::Toml(source) => ConfigError::Parse {
            path: path.into(),
            source,
        },
        ParseError::Rule(e) => e,
    })
}

enum ParseError {
    Toml(toml::de::Error),
    Rule(ConfigError),
}

fn parse(text: &str) -> Result<Config, ParseError> {
    let config: Config = toml::from_str(text).map_err(ParseError::Toml)?;

    if !crate::url_rules::is_allowed(&config.service_url) {
        return Err(ParseError::Rule(ConfigError::Insecure(config.service_url)));
    }
    if config.service_url.query().is_some() || config.service_url.fragment().is_some() {
        return Err(ParseError::Rule(ConfigError::QueryOrFragment(config.service_url)));
    }
    if config.overlay.offset > MAX_OVERLAY_OFFSET {
        return Err(ParseError::Rule(ConfigError::OverlayOffset(config.overlay.offset)));
    }

    Ok(config)
}

fn write_template(path: &Path) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, TEMPLATE)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn minimal_file_gets_the_default_interval() {
        let Ok(config) = parse(r#"service_url = "https://example.com""#) else {
            panic!()
        };
        assert_eq!(config.upload_interval_seconds, DEFAULT_UPLOAD_INTERVAL_SECONDS);
        assert_eq!(config.save_dir, None);
        assert_eq!(config.overlay, Overlay::default());
    }

    #[test]
    fn full_file_is_read() {
        let Ok(config) = parse(
            r#"
            service_url = "http://localhost:5080"
            upload_interval_seconds = 30
            save_dir = "/tmp/saves"
            steam_root = "/tmp/steam"

            [overlay]
            enabled = false
            anchor = "bottom-right"
            offset = 0
            "#,
        ) else {
            panic!()
        };
        assert_eq!(config.upload_interval_seconds, 30);
        assert_eq!(
            config.overlay,
            Overlay {
                enabled: false,
                anchor: Anchor::BottomRight,
                offset: 0,
            }
        );
        assert_eq!(config.save_dir.as_deref(), Some(Path::new("/tmp/saves")));
    }

    #[test]
    fn http_to_a_remote_host_is_refused() {
        assert!(matches!(
            parse(r#"service_url = "http://example.com""#),
            Err(ParseError::Rule(ConfigError::Insecure(_)))
        ));
    }

    #[test]
    fn overlay_offset_above_the_limit_is_refused() {
        assert!(matches!(
            parse("service_url = \"https://example.com\"\n[overlay]\noffset = 91"),
            Err(ParseError::Rule(ConfigError::OverlayOffset(91)))
        ));
    }

    #[test]
    fn missing_file_creates_the_template() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("custos").join("config.toml");

        assert!(matches!(load(&path), Err(ConfigError::Created(_))));
        assert_eq!(std::fs::read_to_string(&path).unwrap(), TEMPLATE);
        assert!(matches!(load(&path), Err(ConfigError::Incomplete { .. })));
    }

    #[test]
    fn template_examples_are_valid() {
        let text: Vec<_> = TEMPLATE
            .lines()
            .map(|line| {
                line.strip_prefix("# ")
                    .filter(|l| l.contains(" = ") || l.starts_with('['))
                    .unwrap_or(line)
            })
            .collect();
        assert!(parse(&text.join("\n")).is_ok());
    }

    #[test]
    fn unknown_keys_are_refused() {
        assert!(matches!(
            parse("service_url = \"https://example.com\"\nservice_ulr = 1"),
            Err(ParseError::Toml(_))
        ));
    }
}
