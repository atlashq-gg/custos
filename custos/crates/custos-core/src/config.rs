//! The configuration file: `$XDG_CONFIG_HOME/custos/config.toml` on Linux, and
//! `%APPDATA%\custos\config.toml` on Windows.

use std::path::{Path, PathBuf};
use std::time::Duration;

use serde::Deserialize;
use tokio::sync::mpsc;
use url::Url;

/// The interval between two uploads when the file does not set one.
pub const DEFAULT_UPLOAD_INTERVAL_SECONDS: u64 = 300;
/// The time with no change of the file before an upload, when the file does not set one.
pub const DEFAULT_UPLOAD_DELAY_SECONDS: u64 = 10;
/// The time after the start of the watch when Custos ignores the changes of the file, when the file
/// does not set one.
pub const DEFAULT_UPLOAD_START_DELAY_SECONDS: u64 = 60;

/// The service of Custos when the file sets none.
pub const DEFAULT_SERVICE_URL: &str = "https://optio.atlashq.gg";

/// The content of a new configuration file, with the paths of this user. Custos writes it when no file
/// exists. Each value is the default, or an example in a comment.
#[must_use]
pub fn template() -> String {
    format!(
        r#"# Custos configuration.

# The URL of the service. Custos gets all other settings from its discovery document.
# service_url = "{DEFAULT_SERVICE_URL}"

# The shortest time between two uploads, in seconds. The service can set a longer time.
# upload_interval_seconds = {DEFAULT_UPLOAD_INTERVAL_SECONDS}

# The time with no change of the map data file before Custos sends it, in seconds. Each change starts
# this time again, so that a set of map refreshes goes in one upload.
# upload_delay_seconds = {DEFAULT_UPLOAD_DELAY_SECONDS}

# The time after the start of Foxhole when Custos ignores the changes of the map data file, in seconds.
# The file is old when the game starts. Custos sends the first change after this time.
# upload_start_delay_seconds = {DEFAULT_UPLOAD_START_DELAY_SECONDS}
{platform}{rest}"#,
        platform = platform_template(),
        rest = REST_OF_TEMPLATE,
    )
}

/// The lines of the template about the save folder, with the paths of this user.
#[cfg(not(windows))]
fn platform_template() -> String {
    let steam = dirs::home_dir()
        .unwrap_or_else(|| PathBuf::from("/home/user"))
        .join(".local/share/Steam");
    let save_dir = steam.join(crate::locate::SAVE_DIR_IN_LIBRARY);
    format!(
        r"
# The folder that holds the save files of Foxhole. Custos finds it in the Proton prefix when this is not set.
# save_dir = {}

# The Steam folder. Custos tries the usual locations when this is not set.
# steam_root = {}
",
        toml_path(&save_dir),
        toml_path(&steam)
    )
}

/// The lines of the template about the save folder, with the default folder of this user.
#[cfg(windows)]
fn platform_template() -> String {
    let save_dir = crate::locate::windows_save_dir()
        .unwrap_or_else(|| PathBuf::from(r"C:\Users\user\AppData\Local\Foxhole\Saved\SaveGames"));
    format!(
        r"
# The folder that holds the save files of Foxhole. Default: %LOCALAPPDATA%\Foxhole\Saved\SaveGames.
# save_dir = {}
",
        toml_path(&save_dir)
    )
}

/// A path as a TOML string, with the escapes that it needs.
fn toml_path(path: &Path) -> String {
    toml::Value::String(path.display().to_string()).to_string()
}

/// The lines of the template that are the same on each platform.
const REST_OF_TEMPLATE: &str = r#"
# The overlay shows the messages above the game while Foxhole runs. Without it, Custos shows
# desktop notifications.
# [overlay]
# enabled = true
# The corner of the screen: "top-left", "top-right", "bottom-left", or "bottom-right".
# anchor = "top-left"
# The distance from the top or bottom edge, in percent of the monitor height, from 0 to 90. In the
# top left corner, the default puts the overlay below the notifications of the game.
# offset = 28
# The most messages, and the most lines of text, that the overlay shows, from 1 to 30. The stricter
# limit applies. An older line is hidden, and it comes back when a newer message goes.
# messages = 6
# lines = 12

# The query: a question to the service in plain language, if the service has a query.
# [query]
# The key that starts a question at the prompt of the overlay. The desktop can ask you to confirm it, and its settings can
# change it later.
# key = "Ctrl+Alt+K"
# The time that the answer stays in the overlay, in seconds, from 1 to 3600. A new question replaces
# it before that time.
# answer_seconds = 180
# Show the key and /help in the overlay, in amber.
# hint = true
"#;

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Config {
    #[serde(default = "default_service_url")]
    pub service_url: Url,
    #[serde(default = "default_interval")]
    pub upload_interval_seconds: u64,
    #[serde(default = "default_delay")]
    pub upload_delay_seconds: u64,
    #[serde(default = "default_start_delay")]
    pub upload_start_delay_seconds: u64,
    pub save_dir: Option<PathBuf>,
    /// Linux only.
    pub steam_root: Option<PathBuf>,
    #[serde(default)]
    pub overlay: Overlay,
    #[serde(default)]
    pub query: Query,
}

/// The `[query]` table.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Query {
    /// The key that starts a question, for example `Ctrl+Alt+K`.
    pub key: String,
    /// The time that the answer stays in the overlay, in seconds.
    pub answer_seconds: u16,
    /// True when the overlay shows the key and `/help`.
    pub hint: bool,
}

/// The largest value of [`Query::answer_seconds`].
pub const MAX_ANSWER_SECONDS: u16 = 3600;

impl Default for Query {
    fn default() -> Self {
        Self {
            key: "Ctrl+Alt+K".into(),
            answer_seconds: 180,
            hint: true,
        }
    }
}

impl Query {
    /// The key. [`load`] makes sure that it is valid.
    #[must_use]
    pub fn shortcut(&self) -> crate::shortcut::Shortcut {
        crate::shortcut::Shortcut::parse(&self.key).unwrap_or_default()
    }

    #[must_use]
    pub fn answer_lifetime(&self) -> Duration {
        Duration::from_secs(u64::from(self.answer_seconds))
    }
}

/// The `[overlay]` table.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Overlay {
    pub enabled: bool,
    pub anchor: Anchor,
    /// The distance from the top or bottom edge, in percent of the monitor height.
    pub offset: u8,
    /// The most messages that the overlay shows.
    pub messages: u8,
    /// The most lines of text that the overlay shows. A line that wraps counts as one.
    pub lines: u8,
}

/// The largest value of [`Overlay::offset`].
pub const MAX_OVERLAY_OFFSET: u8 = 90;
/// The largest value of [`Overlay::messages`] and [`Overlay::lines`].
pub const MAX_OVERLAY_LIMIT: u8 = 30;

impl Default for Overlay {
    fn default() -> Self {
        Self {
            enabled: true,
            anchor: Anchor::TopLeft,
            offset: 28,
            messages: 6,
            lines: 12,
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

fn default_service_url() -> Url {
    Url::parse(DEFAULT_SERVICE_URL).expect("the default service URL is valid")
}

fn default_interval() -> u64 {
    DEFAULT_UPLOAD_INTERVAL_SECONDS
}

fn default_delay() -> u64 {
    DEFAULT_UPLOAD_DELAY_SECONDS
}

fn default_start_delay() -> u64 {
    DEFAULT_UPLOAD_START_DELAY_SECONDS
}

#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
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
    #[error("overlay.{0} must be from 1 to {MAX_OVERLAY_LIMIT}: {1}")]
    OverlayLimit(&'static str, u8),
    #[error("query.key is {0}")]
    QueryKey(String),
    #[error("query.answer_seconds must be from 1 to {MAX_ANSWER_SECONDS}: {0}")]
    AnswerSeconds(u16),
    #[error("cannot find the configuration folder of this user")]
    NoConfigDir,
}

/// The path of the configuration file.
pub fn default_path() -> Result<PathBuf, ConfigError> {
    dirs::config_dir()
        .map(|dir| dir.join("custos").join("config.toml"))
        .ok_or(ConfigError::NoConfigDir)
}

/// The time that Custos waits after a change of the file, because an editor can write a file in
/// several steps.
const SETTLE: Duration = Duration::from_millis(500);

/// Reads the configuration. If the file does not exist, writes the [`template`] and reads it.
pub fn load(path: &Path) -> Result<Config, ConfigError> {
    match read(path) {
        Err(ConfigError::Io { source, .. }) if source.kind() == std::io::ErrorKind::NotFound => {
            write_template(path).map_err(|source| ConfigError::Io {
                path: path.into(),
                source,
            })?;
            tracing::info!("Custos wrote the configuration file {}", path.display());
            read(path)
        }
        result => result,
    }
}

/// Watches the configuration file. After each change, sends the configuration, or the error when the
/// file is not valid. A missing file sends nothing. Runs until the receiver closes.
pub async fn follow(path: PathBuf, changes: mpsc::UnboundedSender<Result<Config, ConfigError>>) {
    let (sender, mut file_events) = mpsc::unbounded_channel();
    let _watcher = match crate::watch::file(&path, sender) {
        Ok(watcher) => watcher,
        Err(e) => return tracing::warn!("cannot watch {}: {e}", path.display()),
    };
    while file_events.recv().await.is_some() {
        tokio::time::sleep(SETTLE).await;
        while file_events.try_recv().is_ok() {}
        let result = read(&path);
        if matches!(&result, Err(ConfigError::Io { source, .. }) if source.kind() == std::io::ErrorKind::NotFound) {
            continue;
        }
        if changes.send(result).is_err() {
            return;
        }
    }
}

/// Reads the configuration.
fn read(path: &Path) -> Result<Config, ConfigError> {
    let text = std::fs::read_to_string(path).map_err(|source| ConfigError::Io {
        path: path.into(),
        source,
    })?;

    parse(&text).map_err(|e| match e {
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
    for (name, value) in [("messages", config.overlay.messages), ("lines", config.overlay.lines)] {
        if !(1..=MAX_OVERLAY_LIMIT).contains(&value) {
            return Err(ParseError::Rule(ConfigError::OverlayLimit(name, value)));
        }
    }

    if let Err(e) = crate::shortcut::Shortcut::parse(&config.query.key) {
        return Err(ParseError::Rule(ConfigError::QueryKey(e)));
    }
    if !(1..=MAX_ANSWER_SECONDS).contains(&config.query.answer_seconds) {
        return Err(ParseError::Rule(ConfigError::AnswerSeconds(
            config.query.answer_seconds,
        )));
    }

    Ok(config)
}

fn write_template(path: &Path) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, template())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn minimal_file_gets_the_default_interval_and_delays() {
        let Ok(config) = parse(r#"service_url = "https://example.com""#) else {
            panic!()
        };
        assert_eq!(config.upload_interval_seconds, DEFAULT_UPLOAD_INTERVAL_SECONDS);
        assert_eq!(config.upload_delay_seconds, DEFAULT_UPLOAD_DELAY_SECONDS);
        assert_eq!(config.upload_start_delay_seconds, DEFAULT_UPLOAD_START_DELAY_SECONDS);
        assert_eq!(config.save_dir, None);
        assert_eq!(config.overlay, Overlay::default());
    }

    #[test]
    fn full_file_is_read() {
        let Ok(config) = parse(
            r#"
            service_url = "http://localhost:5080"
            upload_interval_seconds = 30
            upload_delay_seconds = 5
            upload_start_delay_seconds = 120
            save_dir = "/tmp/saves"
            steam_root = "/tmp/steam"

            [overlay]
            enabled = false
            anchor = "bottom-right"
            offset = 0
            messages = 2
            lines = 30
            "#,
        ) else {
            panic!()
        };
        assert_eq!(config.upload_interval_seconds, 30);
        assert_eq!(config.upload_delay_seconds, 5);
        assert_eq!(config.upload_start_delay_seconds, 120);
        assert_eq!(
            config.overlay,
            Overlay {
                enabled: false,
                anchor: Anchor::BottomRight,
                offset: 0,
                messages: 2,
                lines: 30,
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
    fn overlay_limits_out_of_range_are_refused() {
        assert!(matches!(
            parse("service_url = \"https://example.com\"\n[overlay]\nmessages = 0"),
            Err(ParseError::Rule(ConfigError::OverlayLimit("messages", 0)))
        ));
        assert!(matches!(
            parse("service_url = \"https://example.com\"\n[overlay]\nlines = 31"),
            Err(ParseError::Rule(ConfigError::OverlayLimit("lines", 31)))
        ));
    }

    #[tokio::test]
    async fn changes_of_the_file_are_sent() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("config.toml");
        std::fs::write(&path, "service_url = \"https://example.com\"").unwrap();
        let (changes, mut received) = mpsc::unbounded_channel();
        tokio::spawn(follow(path.clone(), changes));
        // The watch starts in the task.
        tokio::time::sleep(Duration::from_millis(100)).await;

        let mut next = async || {
            tokio::time::timeout(Duration::from_secs(5), received.recv())
                .await
                .expect("the change did not arrive")
                .unwrap()
        };
        std::fs::write(
            &path,
            "service_url = \"https://example.com\"\nupload_interval_seconds = 60",
        )
        .unwrap();
        assert_eq!(next().await.unwrap().upload_interval_seconds, 60);

        std::fs::write(&path, "service_url = 1").unwrap();
        assert!(matches!(next().await, Err(ConfigError::Parse { .. })));
    }

    #[test]
    fn missing_file_creates_the_template_with_the_defaults() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("custos").join("config.toml");

        let config = load(&path).unwrap();
        assert_eq!(std::fs::read_to_string(&path).unwrap(), template());
        assert_eq!(config.service_url.as_str(), "https://optio.atlashq.gg/");
        assert_eq!(config, parse("").ok().unwrap());
    }

    #[test]
    fn template_has_the_paths_of_this_user() {
        let template = template();
        let home = dirs::home_dir().unwrap().display().to_string();
        let local = dirs::data_local_dir().unwrap().display().to_string();
        assert!(template.contains(&home) || template.contains(&local), "{template}");
        assert!(!template.contains("/home/user"));
    }

    #[test]
    fn template_examples_are_valid_and_are_the_defaults() {
        let template = template();
        let text: Vec<_> = template
            .lines()
            .map(|line| {
                line.strip_prefix("# ")
                    .filter(|l| l.contains(" = ") || l.starts_with('['))
                    .unwrap_or(line)
            })
            .collect();
        let Ok(config) = parse(&text.join("\n")) else { panic!() };
        let defaults = parse("").ok().unwrap();
        assert_eq!(
            (
                config.service_url,
                config.upload_interval_seconds,
                config.upload_delay_seconds,
                config.upload_start_delay_seconds,
                config.overlay,
                config.query
            ),
            (
                defaults.service_url,
                defaults.upload_interval_seconds,
                defaults.upload_delay_seconds,
                defaults.upload_start_delay_seconds,
                defaults.overlay,
                defaults.query
            )
        );
    }

    #[test]
    fn query_table_is_read_and_checked() {
        let Ok(config) = parse("service_url = \"https://example.com\"\n[query]\nkey = \"alt+super+q\"") else {
            panic!()
        };
        assert_eq!(config.query.shortcut().to_string(), "Alt+Super+Q");
        assert_eq!(config.query.answer_lifetime(), Duration::from_secs(180));
        assert!(matches!(
            parse("service_url = \"https://example.com\"\n[query]\nkey = \"Q\""),
            Err(ParseError::Rule(ConfigError::QueryKey(_)))
        ));
        assert!(matches!(
            parse("service_url = \"https://example.com\"\n[query]\nanswer_seconds = 0"),
            Err(ParseError::Rule(ConfigError::AnswerSeconds(0)))
        ));
    }

    #[test]
    fn unknown_keys_are_refused() {
        assert!(matches!(
            parse("service_url = \"https://example.com\"\nservice_ulr = 1"),
            Err(ParseError::Toml(_))
        ));
    }
}
