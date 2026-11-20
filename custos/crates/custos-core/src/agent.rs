//! The loop of Custos. It watches the game and the save file, sends uploads, and keeps the login. The
//! application sends [`Command`]s and shows the [`Event`]s.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use notify::{RecommendedWatcher, RecursiveMode, Watcher};
use sha2::{Digest, Sha256};
use tokio::sync::mpsc;

use crate::auth::{self, PendingLogin, TokenStore, Tokens};
use crate::config::Config;
use crate::discovery::{self, Service};
use crate::locate;
use crate::process;
use crate::schedule::Schedule;
use crate::stream::{Kind, Notification, Reference};
use crate::upload::{self, Outcome, UploadResult};

/// An identical error notification is shown at most one time in this period.
const ERROR_REPEAT: Duration = Duration::from_secs(15 * 60);
/// The wait before Custos gets the discovery document again after a failure.
const DISCOVERY_RETRY: Duration = Duration::from_secs(60);

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Command {
    Pause,
    Resume,
    /// Starts a login. Custos answers with [`Event::OpenUrl`].
    SignIn,
    SignOut,
    /// A deep link that the desktop gave to Custos.
    Link(String),
    Shutdown,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum State {
    /// The discovery document is not available.
    Offline,
    /// No token. The user must sign in.
    SignedOut,
    /// Foxhole does not run. Custos does nothing.
    Idle,
    /// Foxhole runs, and Custos watches the save file.
    Watching,
    Paused,
}

impl State {
    #[must_use]
    pub fn label(&self) -> &'static str {
        match self {
            State::Offline => "Service not available",
            State::SignedOut => "Signed out",
            State::Idle => "Idle: Foxhole is not running",
            State::Watching => "Watching the save file",
            State::Paused => "Paused",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Event {
    State(State),
    /// Foxhole started (`true`) or stopped (`false`).
    GameRunning(bool),
    Notify(Notification),
    /// The message with this reference is no longer correct. Remove it if it is visible.
    Expire(Reference),
    /// Open this URL in the browser.
    OpenUrl(url::Url),
}

/// What the agent needs from its environment. Tests replace the process check, the paths, and the store.
pub struct Settings {
    pub config: Config,
    pub store: Arc<dyn TokenStore>,
    /// True when Foxhole runs.
    pub is_game_running: Box<dyn Fn() -> bool + Send + Sync>,
    /// The Steam folders where Custos searches the save folder when the configuration sets none.
    pub steam_roots: Vec<PathBuf>,
    /// How often the loop runs.
    pub tick: Duration,
    /// How often Custos looks for the Foxhole process.
    pub process_poll: Duration,
}

impl Settings {
    /// The settings of the real desktop: the process list, the save folder, and the credential store.
    ///
    /// On Linux, Custos searches the configured Steam folder, or else the usual Steam folders. On
    /// Windows, the save folder of Foxhole is the default.
    #[must_use]
    pub fn desktop(mut config: Config) -> Self {
        let steam_roots = if cfg!(windows) {
            config.save_dir = config.save_dir.or_else(locate::windows_save_dir);
            Vec::new()
        } else {
            let home = dirs::home_dir().unwrap_or_default();
            config
                .steam_root
                .clone()
                .map_or_else(|| locate::default_steam_roots(&home), |root| vec![root])
        };
        Self {
            store: Arc::new(auth::KeyringStore::new(&config.service_url)),
            config,
            is_game_running: Box::new(process::is_foxhole_running),
            steam_roots,
            tick: Duration::from_secs(1),
            process_poll: Duration::from_secs(10),
        }
    }
}

pub struct Agent {
    settings: Settings,
    events: mpsc::UnboundedSender<Event>,
    http: reqwest::Client,
    service: Option<Service>,
    last_discovery_attempt: Option<Instant>,
    tokens: Option<Tokens>,
    pending: Option<PendingLogin>,
    schedule: Schedule,
    paused: bool,
    game_running: bool,
    last_process_check: Option<Instant>,
    map_file: Option<PathBuf>,
    watcher: Option<RecommendedWatcher>,
    file_events: (mpsc::UnboundedSender<()>, mpsc::UnboundedReceiver<()>),
    dirty: bool,
    state: Option<State>,
    /// The last time that each error showed, by reference and text.
    shown_errors: HashMap<(Reference, String), Instant>,
}

impl Agent {
    #[must_use]
    pub fn new(settings: Settings, events: mpsc::UnboundedSender<Event>) -> Self {
        let interval = Duration::from_secs(settings.config.upload_interval_seconds);
        Self {
            settings,
            events,
            http: reqwest::Client::builder()
                .user_agent(concat!("Custos/", env!("CARGO_PKG_VERSION")))
                .timeout(Duration::from_secs(60))
                .build()
                .unwrap_or_default(),
            service: None,
            last_discovery_attempt: None,
            tokens: None,
            pending: None,
            schedule: Schedule::new(interval),
            paused: false,
            game_running: false,
            last_process_check: None,
            map_file: None,
            watcher: None,
            file_events: mpsc::unbounded_channel(),
            dirty: false,
            state: None,
            shown_errors: HashMap::new(),
        }
    }

    /// Runs until [`Command::Shutdown`] arrives or the command channel closes.
    pub async fn run(mut self, mut commands: mpsc::UnboundedReceiver<Command>) {
        self.tokens = self.load_tokens().await;
        let mut ticker = tokio::time::interval(self.settings.tick);
        ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);

        loop {
            tokio::select! {
                command = commands.recv() => match command {
                    None | Some(Command::Shutdown) => break,
                    Some(command) => self.handle(command).await,
                },
                Some(()) = self.file_events.1.recv() => self.dirty = true,
                _ = ticker.tick() => self.tick().await,
            }
        }
    }

    async fn handle(&mut self, command: Command) {
        match command {
            Command::Pause => self.paused = true,
            Command::Resume => self.paused = false,
            Command::SignIn => self.sign_in(),
            Command::SignOut => self.sign_out().await,
            Command::Link(link) => self.complete_sign_in(&link).await,
            Command::Shutdown => {}
        }
        self.update_state();
    }

    async fn tick(&mut self) {
        let now = Instant::now();

        if self.service.is_none() && self.last_discovery_attempt.is_none_or(|t| now >= t + DISCOVERY_RETRY) {
            self.fetch_discovery(now).await;
        }

        if self
            .last_process_check
            .is_none_or(|t| now >= t + self.settings.process_poll)
        {
            self.last_process_check = Some(now);
            let running = (self.settings.is_game_running)();
            if running != self.game_running {
                self.game_running = running;
                self.emit(Event::GameRunning(running));
                if running {
                    self.start_watching();
                } else {
                    self.stop_watching();
                }
            }
        }

        self.update_state();

        if self.state == Some(State::Watching) && self.dirty && self.schedule.is_due(now) {
            self.upload(now).await;
        }
    }

    fn update_state(&mut self) {
        let state = if self.paused {
            State::Paused
        } else if self.service.is_none() {
            State::Offline
        } else if self.tokens.is_none() {
            State::SignedOut
        } else if !self.game_running || self.map_file.is_none() {
            State::Idle
        } else {
            State::Watching
        };

        if self.state.as_ref() != Some(&state) {
            if state == State::SignedOut && self.state.is_some() {
                self.notify_error(
                    Kind::SignInRequired,
                    "Sign in to send stockpile data. Use the Custos menu in the tray.",
                );
            }
            self.state = Some(state.clone());
            self.emit(Event::State(state));
        }
    }

    async fn fetch_discovery(&mut self, now: Instant) {
        self.last_discovery_attempt = Some(now);
        match discovery::fetch(&self.http, &self.settings.config.service_url).await {
            Ok(service) => {
                self.schedule
                    .set_service_minimum(Duration::from_secs(service.discovery.upload_min_interval_seconds));
                self.service = Some(service);
            }
            Err(e) => self.notify_error(Kind::ServiceError, &e.to_string()),
        }
    }

    fn start_watching(&mut self) {
        let located = locate::find_save_dir(self.settings.config.save_dir.as_deref(), &self.settings.steam_roots)
            .and_then(|dir| locate::find_map_data(&dir).map(|file| (dir, file)));
        let (dir, file) = match located {
            Ok(found) => found,
            Err(e) => return self.notify_error(Kind::SaveFile, &e.to_string()),
        };

        match watch(&dir, &file, self.file_events.0.clone()) {
            Ok(watcher) => self.watcher = Some(watcher),
            Err(e) => self.notify_error(Kind::SaveFile, &format!("cannot watch {}: {e}", dir.display())),
        }
        tracing::info!("watching {}", file.display());
        self.map_file = Some(file);
        // Send the current file when it differs from the last accepted one.
        self.dirty = true;
    }

    fn stop_watching(&mut self) {
        self.watcher = None;
        self.map_file = None;
        self.dirty = false;
    }

    async fn upload(&mut self, now: Instant) {
        let (Some(file), Some(service)) = (self.map_file.clone(), self.service.clone()) else {
            return;
        };

        let bytes = match tokio::fs::read(&file).await {
            Ok(bytes) => bytes,
            Err(e) => return self.notify_error(Kind::UploadError, &format!("cannot read {}: {e}", file.display())),
        };
        let sha256 = hex::encode(Sha256::digest(&bytes));
        if !self.schedule.is_new(&sha256) {
            self.dirty = false;
            return;
        }
        if bytes.len() as u64 > service.discovery.upload_max_bytes {
            self.schedule.rejected(sha256);
            return self.notify_error(
                Kind::UploadError,
                &format!(
                    "The map data file is larger than the limit of {} ({} bytes).",
                    service.discovery.service_name, service.discovery.upload_max_bytes
                ),
            );
        }

        if !self.ensure_fresh_token(&service).await {
            return;
        }

        let mut outcome = self.send(&service, &bytes).await;
        if outcome == Some(Outcome::Unauthorized) {
            // The token stopped working before its expiry. Refresh one time and send again.
            outcome = if self.refresh_tokens(&service).await {
                self.send(&service, &bytes).await
            } else {
                None
            };
            if outcome == Some(Outcome::Unauthorized) {
                self.forget_tokens().await;
            }
        }
        let Some(outcome) = outcome else { return };
        self.schedule.sent(now);

        match outcome {
            Outcome::Accepted(result) => {
                self.schedule.accepted(sha256);
                self.dirty = false;
                self.expire(Kind::UploadError);
                self.expire(Kind::ServiceError);
                self.notify(accepted_notification(&service.discovery.service_name, &result));
            }
            Outcome::Unauthorized => {}
            Outcome::NotFound => {
                self.service = None;
                self.last_discovery_attempt = None;
            }
            Outcome::Rejected { status, problem } => {
                self.schedule.rejected(sha256);
                self.dirty = false;
                self.notify_error(
                    Kind::UploadError,
                    &problem.text(&format!("The service refused the file ({status}).")),
                );
            }
            Outcome::RateLimited { retry_after } => self.schedule.retry_after(now, retry_after),
            Outcome::Failed { status, problem } => {
                self.notify_error(
                    Kind::ServiceError,
                    &problem.text(&format!("The service failed ({status}). Custos tries again later.")),
                );
            }
        }
    }

    /// Sends the file. `None` means a transport error, which is already reported.
    async fn send(&mut self, service: &Service, bytes: &[u8]) -> Option<Outcome> {
        let token = self.tokens.as_ref()?.access_token.clone();
        match upload::send(&self.http, &service.discovery.upload_endpoint, &token, bytes.to_vec()).await {
            Ok(outcome) => Some(outcome),
            Err(e) => {
                self.notify_error(
                    Kind::ServiceError,
                    &format!("cannot reach {}: {e}", service.discovery.service_name),
                );
                None
            }
        }
    }

    async fn ensure_fresh_token(&mut self, service: &Service) -> bool {
        match &self.tokens {
            None => false,
            Some(tokens) if tokens.is_expiring(auth::unix_now()) => self.refresh_tokens(service).await,
            Some(_) => true,
        }
    }

    /// Gets a new access token. If that fails, Custos forgets the tokens and the user must sign in.
    async fn refresh_tokens(&mut self, service: &Service) -> bool {
        let Some(refresh_token) = self.tokens.as_ref().and_then(|t| t.refresh_token.clone()) else {
            self.forget_tokens().await;
            return false;
        };

        match auth::refresh(&self.http, service, &refresh_token).await {
            Ok(tokens) => {
                self.save_tokens(tokens).await;
                true
            }
            Err(e) => {
                tracing::warn!("refresh failed: {e}");
                self.forget_tokens().await;
                false
            }
        }
    }

    fn sign_in(&mut self) {
        let Some(service) = &self.service else {
            return self.notify_error(
                Kind::Login,
                "The service is not available. Custos cannot start the login.",
            );
        };
        let pending = auth::begin(service);
        self.emit(Event::OpenUrl(pending.authorize_url.clone()));
        self.pending = Some(pending);
    }

    async fn complete_sign_in(&mut self, link: &str) {
        if !auth::is_callback(link) {
            return tracing::debug!("ignored link {link}");
        }
        let (Some(pending), Some(service)) = (self.pending.take(), self.service.clone()) else {
            return self.notify_error(Kind::Login, "No login is in progress. Use Sign in in the Custos menu.");
        };

        let result = match auth::code_from_callback(&pending, link) {
            Ok(code) => auth::exchange_code(&self.http, &service, &pending, &code).await,
            Err(e) => Err(e),
        };
        match result {
            Ok(tokens) => {
                self.save_tokens(tokens).await;
                self.expire(Kind::SignInRequired);
                self.notify(Notification {
                    reference: Kind::Login.into(),
                    title: "Signed in".into(),
                    body: format!("Custos sends stockpile data to {}.", service.discovery.service_name),
                    is_error: false,
                });
            }
            Err(e) => self.notify_error(Kind::Login, &e.to_string()),
        }
    }

    async fn sign_out(&mut self) {
        if let (Some(tokens), Some(service)) = (&self.tokens, &self.service) {
            auth::revoke(&self.http, service, tokens).await;
        }
        self.forget_tokens().await;
    }

    async fn load_tokens(&self) -> Option<Tokens> {
        let store = self.settings.store.clone();
        match tokio::task::spawn_blocking(move || store.load()).await {
            Ok(Ok(tokens)) => tokens,
            Ok(Err(e)) => {
                tracing::warn!("{e}");
                None
            }
            Err(e) => {
                tracing::warn!("{e}");
                None
            }
        }
    }

    async fn save_tokens(&mut self, tokens: Tokens) {
        let store = self.settings.store.clone();
        let copy = tokens.clone();
        if let Ok(Err(e)) = tokio::task::spawn_blocking(move || store.save(&copy)).await {
            // The session still works until Custos stops.
            self.notify_error(Kind::TokenStore, &e.to_string());
        }
        self.tokens = Some(tokens);
    }

    async fn forget_tokens(&mut self) {
        self.tokens = None;
        let store = self.settings.store.clone();
        if let Ok(Err(e)) = tokio::task::spawn_blocking(move || store.clear()).await {
            tracing::warn!("{e}");
        }
    }

    fn notify(&self, notification: Notification) {
        self.emit(Event::Notify(notification));
    }

    /// Shows an error, but not an identical error that showed less than [`ERROR_REPEAT`] before.
    fn notify_error(&mut self, kind: Kind, message: &str) {
        let now = Instant::now();
        let key = (Reference::new(kind), message.to_owned());
        if self
            .shown_errors
            .get(&key)
            .is_some_and(|shown| now < *shown + ERROR_REPEAT)
        {
            return;
        }
        tracing::warn!("{message}");
        self.shown_errors.insert(key.clone(), now);
        self.notify(Notification {
            reference: key.0,
            title: "Custos".into(),
            body: message.to_owned(),
            is_error: true,
        });
    }

    /// Removes the message of this kind. The error can then show again at once if it occurs again.
    fn expire(&mut self, kind: Kind) {
        let reference = Reference::new(kind);
        self.shown_errors.retain(|(r, _), _| *r != reference);
        self.emit(Event::Expire(reference));
    }

    fn emit(&self, event: Event) {
        // The receiver is gone only when the application stops.
        let _ = self.events.send(event);
    }
}

/// Watches the save folder. A change to the map data file sends `()`. The folder is watched, not the
/// file, because a game can replace the file with a new one.
fn watch(dir: &Path, file: &Path, changed: mpsc::UnboundedSender<()>) -> notify::Result<RecommendedWatcher> {
    let name = file.file_name().map(ToOwned::to_owned);
    let mut watcher = notify::recommended_watcher(move |event: notify::Result<notify::Event>| {
        if let Ok(event) = event
            && !event.kind.is_access()
            && event.paths.iter().any(|p| p.file_name() == name.as_deref())
        {
            let _ = changed.send(());
        }
    })?;
    watcher.watch(dir, RecursiveMode::NonRecursive)?;
    Ok(watcher)
}

fn accepted_notification(service: &str, result: &UploadResult) -> Notification {
    use std::fmt::Write;

    let mut body = format!("{service} received {} KB.", result.size_bytes.div_ceil(1024));
    if let Some(war) = &result.war {
        let _ = write!(body, " War {}.", war.war_number);
    }
    if !result.stockpiles.is_empty() {
        let names: Vec<_> = result.stockpiles.iter().map(|s| s.name.as_str()).collect();
        let _ = write!(body, " Stockpiles: {}.", names.join(", "));
    }
    Notification {
        reference: Kind::Upload.into(),
        title: "Stockpile data sent".into(),
        body,
        is_error: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::auth::MemoryStore;
    use std::sync::atomic::{AtomicBool, Ordering};
    use wiremock::matchers::{header, method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    const RESULT: &str = r#"{"status":"accepted","received_at":"2026-11-17T14:05:00Z","size_bytes":8,
        "sha256":"0000000000000000000000000000000000000000000000000000000000000000",
        "war":{"war_id":"w","war_number":142},"stockpiles":[]}"#;

    struct Fixture {
        server: MockServer,
        events: mpsc::UnboundedReceiver<Event>,
        commands: mpsc::UnboundedSender<Command>,
        save_dir: tempfile::TempDir,
        game: Arc<AtomicBool>,
        store: Arc<MemoryStore>,
    }

    impl Fixture {
        async fn start(tokens: Option<Tokens>) -> Self {
            let server =
                crate::discovery::tests::mock_service(|d| d["upload_min_interval_seconds"] = 0.into(), |_| {}).await;

            let save_dir = tempfile::tempdir().unwrap();
            std::fs::write(save_dir.path().join("1_MapData.sav"), b"GVAS0001").unwrap();
            let game = Arc::new(AtomicBool::new(false));
            let store = Arc::new(MemoryStore::default());
            if let Some(tokens) = &tokens {
                store.save(tokens).unwrap();
            }

            // A literal string, because a path on Windows holds backslashes.
            let config: Config = toml::from_str(&format!(
                "service_url = \"{}\"\nupload_interval_seconds = 0\nsave_dir = '{}'",
                server.uri(),
                save_dir.path().display()
            ))
            .unwrap();
            let settings = Settings {
                config,
                store: store.clone(),
                is_game_running: Box::new({
                    let game = game.clone();
                    move || game.load(Ordering::Relaxed)
                }),
                steam_roots: vec![],
                tick: Duration::from_millis(20),
                process_poll: Duration::from_millis(20),
            };

            let (event_tx, events) = mpsc::unbounded_channel();
            let (commands, command_rx) = mpsc::unbounded_channel();
            tokio::spawn(Agent::new(settings, event_tx).run(command_rx));

            Self {
                server,
                events,
                commands,
                save_dir,
                game,
                store,
            }
        }

        fn start_game(&self) {
            self.game.store(true, Ordering::Relaxed);
        }

        fn stop_game(&self) {
            self.game.store(false, Ordering::Relaxed);
        }

        async fn next(&mut self, wanted: impl Fn(&Event) -> bool) -> Event {
            tokio::time::timeout(Duration::from_secs(5), async {
                loop {
                    let event = self.events.recv().await.unwrap();
                    if wanted(&event) {
                        return event;
                    }
                }
            })
            .await
            .expect("the event did not arrive")
        }
    }

    fn tokens(access: &str) -> Tokens {
        Tokens {
            access_token: access.into(),
            refresh_token: Some("r1".into()),
            expires_at: None,
        }
    }

    fn is_state(state: State) -> impl Fn(&Event) -> bool {
        move |e| *e == Event::State(state.clone())
    }

    fn is_notification(e: &Event) -> bool {
        matches!(e, Event::Notify(_))
    }

    #[tokio::test]
    async fn uploads_while_the_game_runs_and_goes_idle_when_it_stops() {
        let mut f = Fixture::start(Some(tokens("a1"))).await;
        Mock::given(method("POST"))
            .and(path("/api/v1/uploads"))
            .and(header("authorization", "Bearer a1"))
            .respond_with(ResponseTemplate::new(200).set_body_raw(RESULT, "application/json"))
            .expect(1)
            .mount(&f.server)
            .await;

        f.next(is_state(State::Idle)).await;
        f.start_game();
        f.next(|e| *e == Event::GameRunning(true)).await;
        f.next(is_state(State::Watching)).await;

        // An accepted upload expires the errors of the upload and of the service.
        f.next(|e| *e == Event::Expire(Kind::UploadError.into())).await;
        f.next(|e| *e == Event::Expire(Kind::ServiceError.into())).await;
        let Event::Notify(n) = f.next(is_notification).await else {
            unreachable!()
        };
        assert_eq!(n.reference, Kind::Upload.into());
        assert_eq!(n.title, "Stockpile data sent");
        assert!(n.body.contains("War 142"), "{}", n.body);

        f.stop_game();
        f.next(|e| *e == Event::GameRunning(false)).await;
        f.next(is_state(State::Idle)).await;
        // The same file is not sent again: wiremock checks expect(1) when the server drops.
    }

    #[tokio::test]
    async fn changed_file_is_sent_again() {
        let mut f = Fixture::start(Some(tokens("a1"))).await;
        Mock::given(method("POST"))
            .respond_with(ResponseTemplate::new(200).set_body_raw(RESULT, "application/json"))
            .expect(2)
            .mount(&f.server)
            .await;

        f.start_game();
        f.next(is_notification).await;
        std::fs::write(f.save_dir.path().join("1_MapData.sav"), b"GVAS0002").unwrap();
        f.next(is_notification).await;
    }

    #[tokio::test]
    async fn rejected_token_is_refreshed_one_time() {
        let mut f = Fixture::start(Some(tokens("old"))).await;
        Mock::given(method("POST"))
            .and(path("/api/v1/uploads"))
            .and(header("authorization", "Bearer old"))
            .respond_with(ResponseTemplate::new(401))
            .mount(&f.server)
            .await;
        Mock::given(method("POST"))
            .and(path("/token"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"access_token": "new"})))
            .mount(&f.server)
            .await;
        Mock::given(method("POST"))
            .and(path("/api/v1/uploads"))
            .and(header("authorization", "Bearer new"))
            .respond_with(ResponseTemplate::new(200).set_body_raw(RESULT, "application/json"))
            .mount(&f.server)
            .await;

        f.start_game();
        let Event::Notify(n) = f.next(is_notification).await else {
            unreachable!()
        };

        assert!(!n.is_error, "{}", n.body);
        assert_eq!(f.store.load().unwrap().unwrap().access_token, "new");
    }

    #[tokio::test]
    async fn failed_refresh_signs_out() {
        let mut f = Fixture::start(Some(tokens("old"))).await;
        Mock::given(method("POST"))
            .and(path("/api/v1/uploads"))
            .respond_with(ResponseTemplate::new(401))
            .mount(&f.server)
            .await;
        Mock::given(method("POST"))
            .and(path("/token"))
            .respond_with(ResponseTemplate::new(400))
            .mount(&f.server)
            .await;

        f.start_game();
        f.next(is_state(State::SignedOut)).await;

        assert_eq!(f.store.load().unwrap(), None);
    }

    #[tokio::test]
    async fn sign_in_opens_the_browser_and_the_link_completes_it() {
        let mut f = Fixture::start(None).await;
        Mock::given(method("POST"))
            .and(path("/token"))
            .respond_with(
                ResponseTemplate::new(200)
                    .set_body_json(serde_json::json!({"access_token": "a1", "refresh_token": "r1"})),
            )
            .mount(&f.server)
            .await;

        f.next(is_state(State::SignedOut)).await;
        f.commands.send(Command::SignIn).unwrap();
        let Event::OpenUrl(url) = f.next(|e| matches!(e, Event::OpenUrl(_))).await else {
            unreachable!()
        };
        let state = url.query_pairs().find(|(k, _)| k == "state").unwrap().1.into_owned();

        f.commands
            .send(Command::Link(format!("custos://oauth2callback/?state={state}&code=c1")))
            .unwrap();
        // A completed login expires the line that asks for the login.
        f.next(|e| *e == Event::Expire(Kind::SignInRequired.into())).await;
        f.next(is_state(State::Idle)).await;

        assert_eq!(f.store.load().unwrap().unwrap().access_token, "a1");
    }

    #[tokio::test]
    async fn paused_agent_does_not_upload() {
        let mut f = Fixture::start(Some(tokens("a1"))).await;
        Mock::given(method("POST"))
            .respond_with(ResponseTemplate::new(200))
            .expect(0)
            .mount(&f.server)
            .await;

        f.commands.send(Command::Pause).unwrap();
        f.next(is_state(State::Paused)).await;
        f.start_game();
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
}
