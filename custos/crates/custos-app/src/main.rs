//! The tray application of Custos: a tray menu, the overlay while Foxhole runs, desktop notifications,
//! the question at the prompt of the overlay, and the deep link `custos://oauth2callback` for the login. The work happens in
//! [`custos_core::agent::Agent`].

// A release build on Windows opens no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod ask;
mod overlay;

use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};

use custos_core::agent::{Agent, Command, Event, Settings, State};
use custos_core::config;
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Manager, RunEvent, Wry};
use tauri_plugin_deep_link::DeepLinkExt;
use tauri_plugin_notification::NotificationExt;
use tauri_plugin_opener::OpenerExt;
use tokio::sync::mpsc;

const TRAY_ID: &str = "custos";

/// The menu items that change, the last state of the agent, and the agent.
struct Tray {
    status: MenuItem<Wry>,
    pause: MenuItem<Wry>,
    sign: MenuItem<Wry>,
    ask: MenuItem<Wry>,
    /// The channel to the agent, and the configuration that it runs with. `None` until the
    /// configuration is valid.
    agent: Mutex<Option<(mpsc::UnboundedSender<Command>, config::Config)>>,
    state: Mutex<Option<State>>,
    game_running: AtomicBool,
    /// The key that starts a question, after Custos asked the desktop for it.
    key: Mutex<Option<String>>,
}

impl Tray {
    fn send(&self, command: Command) {
        if let Some((commands, _)) = &*self.agent.lock().expect("the agent lock is not poisoned") {
            let _ = commands.send(command);
        }
    }

    fn overlay(&self) -> config::Overlay {
        self.agent
            .lock()
            .expect("the agent lock is not poisoned")
            .as_ref()
            .map(|(_, config)| config.overlay)
            .unwrap_or_default()
    }
}

fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info,custos=debug".into()),
        )
        .init();

    let loaded = config::default_path().and_then(|path| config::load(&path));
    #[cfg(target_os = "linux")]
    if let Ok(config) = &loaded {
        restart_with_x11(config.overlay.enabled);
    }

    // The single-instance plugin is first: a second start, for example from the login link, gives its
    // arguments to the first instance, and the deep-link plugin reads the link from them.
    let builder = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|_app, _argv, _cwd| {}))
        .plugin(tauri_plugin_deep_link::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_opener::init());
    #[cfg(windows)]
    let builder = builder.plugin(tauri_plugin_global_shortcut::Builder::new().build());
    let app = builder
        .invoke_handler(tauri::generate_handler![overlay::overlay_key])
        .setup(|app| {
            setup(app.handle(), loaded)?;
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("cannot start Custos");

    app.run(|app, event| {
        // Custos has no window, so no window closing may stop it. Only Quit stops it.
        if let RunEvent::ExitRequested { api, code: None, .. } = event {
            api.prevent_exit();
        } else if let RunEvent::Exit = event {
            app.state::<Tray>().send(Command::Shutdown);
        }
    });
}

/// On GNOME on Wayland, starts Custos again with the X11 backend of GDK, so that the overlay can be an
/// X11 window above the game. The new process replaces this one, so no second instance exists. Returns
/// only when no restart is necessary or the restart fails.
#[cfg(target_os = "linux")]
fn restart_with_x11(overlay_enabled: bool) {
    use std::os::unix::process::CommandExt;

    use custos_core::screen::{X11_MARKER, needs_x11_restart};

    if !needs_x11_restart(|name| std::env::var(name).ok(), overlay_enabled) {
        return;
    }
    // In an AppImage, the executable is in a temporary mount. The AppImage file starts it again.
    let Some(program) = std::env::var_os("APPIMAGE")
        .map(std::path::PathBuf::from)
        .or_else(|| std::env::current_exe().ok())
    else {
        return;
    };
    tracing::info!("GNOME has no layer-shell protocol. Custos starts again with the X11 backend.");
    let error = std::process::Command::new(program)
        .args(std::env::args_os().skip(1))
        .env("GDK_BACKEND", "x11")
        .env(X11_MARKER, "1")
        .exec();
    tracing::warn!("cannot start Custos again with the X11 backend: {error}");
}

fn setup(app: &AppHandle, loaded: Result<config::Config, config::ConfigError>) -> tauri::Result<()> {
    // An AppImage or a development build has no installed desktop file. This registers the scheme for
    // the current binary.
    if let Err(e) = app.deep_link().register_all() {
        tracing::warn!("cannot register the custos:// scheme: {e}");
    }

    let status = MenuItem::with_id(app, "status", "Starting", false, None::<&str>)?;
    let pause = MenuItem::with_id(app, "pause", "Pause", false, None::<&str>)?;
    let sign = MenuItem::with_id(app, "sign", "Sign in", false, None::<&str>)?;
    // Enabled while the overlay is open, because the question is typed at its prompt.
    let ask = MenuItem::with_id(app, "ask", "Ask a question", false, None::<&str>)?;
    let config_item = MenuItem::with_id(app, "config", "Open configuration", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
    let menu = Menu::with_items(
        app,
        &[
            &status,
            &PredefinedMenuItem::separator(app)?,
            &pause,
            &sign,
            &ask,
            &config_item,
            &PredefinedMenuItem::separator(app)?,
            &quit,
        ],
    )?;

    TrayIconBuilder::with_id(TRAY_ID)
        .icon(app.default_window_icon().cloned().expect("the bundle has an icon"))
        .tooltip("Custos")
        .menu(&menu)
        .show_menu_on_left_click(true)
        .on_menu_event(|app, event| on_menu_event(app, &event))
        .build(app)?;

    app.manage(Tray {
        status,
        pause,
        sign,
        ask,
        agent: Mutex::new(None),
        state: Mutex::new(None),
        game_running: AtomicBool::new(false),
        key: Mutex::new(None),
    });

    match loaded {
        Ok(config) => apply(app, config),
        Err(e) => {
            app.state::<Tray>().status.set_text("Not configured")?;
            notify(app, "Custos", &e.to_string());
        }
    }
    if let Ok(path) = config::default_path() {
        follow_configuration(app, path);
    }

    // A login link that started Custos itself, and links that arrive later.
    let handle = app.clone();
    app.deep_link().on_open_url(move |event| {
        for url in event.urls() {
            handle.state::<Tray>().send(Command::Link(url.to_string()));
        }
    });
    if let Ok(Some(urls)) = app.deep_link().get_current() {
        for url in urls {
            app.state::<Tray>().send(Command::Link(url.to_string()));
        }
    }

    Ok(())
}

/// Applies each change of the configuration file. Custos keeps the last valid configuration when the
/// file is not valid.
fn follow_configuration(app: &AppHandle, path: std::path::PathBuf) {
    let (changes, mut received) = mpsc::unbounded_channel();
    tauri::async_runtime::spawn(config::follow(path, changes));
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        while let Some(result) = received.recv().await {
            match result {
                Ok(config) => apply(&app, config),
                Err(e) => notify(&app, "Custos", &e.to_string()),
            }
        }
    });
}

/// Applies a configuration. The first configuration and a new service URL start a new agent, because
/// the tokens belong to the service. Other changes go to the running agent.
fn apply(app: &AppHandle, config: config::Config) {
    let tray = app.state::<Tray>();
    let mut agent = tray.agent.lock().expect("the agent lock is not poisoned");
    let old = agent.take();
    if old.as_ref().is_some_and(|(_, old)| *old == config) {
        *agent = old;
        return;
    }
    let overlay_changed = old.as_ref().is_some_and(|(_, old)| old.overlay != config.overlay);
    let commands = match old {
        Some((commands, old)) if old.service_url == config.service_url => {
            let _ = commands.send(Command::Reconfigure(Box::new(config.clone())));
            commands
        }
        old => {
            if let Some((commands, _)) = old {
                let _ = commands.send(Command::Shutdown);
            }
            start_agent(app, config.clone())
        }
    };
    tracing::info!("applied the configuration");
    let query = config.query.clone();
    *agent = Some((commands, config));
    drop(agent);

    apply_query(app, &query);

    if overlay_changed && let Err(e) = update_overlay(app, tray.game_running.load(Ordering::Relaxed)) {
        tracing::warn!("cannot update the overlay: {e}");
    }
}

/// Applies the `[query]` table. Custos asks the desktop for the key one time: the desktop keeps the
/// key, and its settings change it. On Windows, a new key replaces the old one at once.
fn apply_query(app: &AppHandle, query: &config::Query) {
    let key = query.shortcut();
    let mut listening = app
        .state::<Tray>()
        .key
        .lock()
        .expect("the key lock is not poisoned")
        .clone();
    let changed = listening.as_deref() != Some(key.to_string().as_str());
    if listening.is_none() || (cfg!(windows) && changed) {
        ask::listen(app, &key);
        listening = Some(key.to_string());
        *app.state::<Tray>().key.lock().expect("the key lock is not poisoned") = listening;
    }
    let query = query.clone();
    if let Err(e) = app.run_on_main_thread(move || ask::configure(&query)) {
        tracing::warn!("cannot apply the query settings: {e}");
    }
}

fn start_agent(app: &AppHandle, config: config::Config) -> mpsc::UnboundedSender<Command> {
    let (commands, command_rx) = mpsc::unbounded_channel();
    let (event_tx, mut events) = mpsc::unbounded_channel();

    tauri::async_runtime::spawn(Agent::new(Settings::desktop(config), event_tx).run(command_rx));

    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        while let Some(event) = events.recv().await {
            if let Err(e) = on_agent_event(&app, event) {
                tracing::warn!("cannot show an event: {e}");
            }
        }
    });

    commands
}

/// The overlay is open while Foxhole runs and the configuration enables it. If it does not open,
/// Custos shows desktop notifications.
fn update_overlay(app: &AppHandle, game_running: bool) -> tauri::Result<()> {
    let settings = app.state::<Tray>().overlay();
    let handle = app.clone();
    app.run_on_main_thread(move || {
        if !game_running || !settings.enabled {
            overlay::close();
        } else if let Err(e) = overlay::open(&handle, settings) {
            tracing::warn!("cannot open the overlay: {e}. Custos shows desktop notifications.");
        }
        if let Err(e) = handle.state::<Tray>().ask.set_enabled(overlay::is_open()) {
            tracing::warn!("cannot update the tray menu: {e}");
        }
    })
}

fn on_agent_event(app: &AppHandle, event: Event) -> tauri::Result<()> {
    match event {
        Event::State(state) => {
            let tray = app.state::<Tray>();
            tray.status.set_text(state.label())?;
            tray.pause.set_enabled(true)?;
            tray.pause
                .set_text(if state == State::Paused { "Resume" } else { "Pause" })?;
            tray.sign.set_enabled(state != State::Offline)?;
            tray.sign.set_text(if state == State::SignedOut {
                "Sign in"
            } else {
                "Sign out"
            })?;
            if let Some(icon) = app.tray_by_id(TRAY_ID) {
                icon.set_tooltip(Some(format!("Custos: {}", state.label())))?;
            }
            *tray.state.lock().expect("the state lock is not poisoned") = Some(state);
        }
        Event::GameRunning(running) => {
            app.state::<Tray>().game_running.store(running, Ordering::Relaxed);
            update_overlay(app, running)?;
        }
        // A message goes to the overlay when it is open, else to a desktop notification.
        Event::Notify(n) => {
            let app = app.clone();
            app.clone().run_on_main_thread(move || {
                if let Some(n) = overlay::put(n) {
                    notify(&app, &n.title, &n.body);
                }
            })?;
        }
        Event::Update(n) => app.run_on_main_thread(move || overlay::update(n))?,
        Event::Expire(reference) => app.run_on_main_thread(move || overlay::expire(&reference))?,
        // The query shows in the overlay. When it is not open, a question or an end shows as a
        // desktop notification.
        Event::QueryAvailable(available) => app.run_on_main_thread(move || ask::set_available(available))?,
        Event::Query(query) => {
            let app = app.clone();
            let title = query.question.clone().unwrap_or_else(|| "Custos".into());
            app.clone().run_on_main_thread(move || {
                if let Some(body) = ask::set(&query) {
                    notify(&app, &title, &body);
                }
            })?;
        }
        Event::OpenUrl(url) => {
            if let Err(e) = app.opener().open_url(url.as_str(), None::<&str>) {
                notify(
                    app,
                    "Custos",
                    &format!("Cannot open the browser: {e}. Open this link: {url}"),
                );
            }
        }
    }
    Ok(())
}

fn on_menu_event(app: &AppHandle, event: &tauri::menu::MenuEvent) {
    let tray = app.state::<Tray>();
    let state = tray.state.lock().expect("the state lock is not poisoned").clone();

    match event.id().as_ref() {
        "pause" if state == Some(State::Paused) => tray.send(Command::Resume),
        "pause" => tray.send(Command::Pause),
        "sign" if state == Some(State::SignedOut) => tray.send(Command::SignIn),
        "sign" => tray.send(Command::SignOut),
        "ask" => ask::start(),
        "config" => match config::default_path() {
            Ok(path) => {
                if let Err(e) = app.opener().open_path(path.to_string_lossy(), None::<&str>) {
                    notify(app, "Custos", &format!("Cannot open {}: {e}", path.display()));
                }
            }
            Err(e) => notify(app, "Custos", &e.to_string()),
        },
        "quit" => {
            tray.send(Command::Shutdown);
            app.exit(0);
        }
        _ => {}
    }
}

fn notify(app: &AppHandle, title: &str, body: &str) {
    if let Err(e) = app.notification().builder().title(title).body(body).show() {
        tracing::warn!("cannot show a notification: {e}");
    }
}
