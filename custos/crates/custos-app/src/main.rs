//! The tray application of Custos: a tray menu, the overlay while Foxhole runs, desktop notifications,
//! and the deep link `custos://oauth2callback` for the login. The work happens in
//! [`custos_core::agent::Agent`].

// A release build on Windows opens no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod overlay;

use std::sync::Mutex;

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

/// The menu items that change, the last state of the agent, and the channel to it.
struct Tray {
    status: MenuItem<Wry>,
    pause: MenuItem<Wry>,
    sign: MenuItem<Wry>,
    commands: Option<mpsc::UnboundedSender<Command>>,
    state: Mutex<Option<State>>,
}

impl Tray {
    fn send(&self, command: Command) {
        if let Some(commands) = &self.commands {
            let _ = commands.send(command);
        }
    }
}

fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info,custos=debug".into()),
        )
        .init();

    // The single-instance plugin is first: a second start, for example from the login link, gives its
    // arguments to the first instance, and the deep-link plugin reads the link from them.
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|_app, _argv, _cwd| {}))
        .plugin(tauri_plugin_deep_link::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            setup(app.handle())?;
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

fn setup(app: &AppHandle) -> tauri::Result<()> {
    // An AppImage or a development build has no installed desktop file. This registers the scheme for
    // the current binary.
    if let Err(e) = app.deep_link().register_all() {
        tracing::warn!("cannot register the custos:// scheme: {e}");
    }

    let status = MenuItem::with_id(app, "status", "Starting", false, None::<&str>)?;
    let pause = MenuItem::with_id(app, "pause", "Pause", false, None::<&str>)?;
    let sign = MenuItem::with_id(app, "sign", "Sign in", false, None::<&str>)?;
    let config_item = MenuItem::with_id(app, "config", "Open configuration", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
    let menu = Menu::with_items(
        app,
        &[
            &status,
            &PredefinedMenuItem::separator(app)?,
            &pause,
            &sign,
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

    let commands = match config::default_path().and_then(|path| config::load(&path)) {
        Ok(config) => Some(start_agent(app, config)),
        Err(e) => {
            status.set_text("Not configured")?;
            notify(app, "Custos", &e.to_string());
            None
        }
    };

    app.manage(Tray {
        status,
        pause,
        sign,
        commands,
        state: Mutex::new(None),
    });

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

fn start_agent(app: &AppHandle, config: config::Config) -> mpsc::UnboundedSender<Command> {
    let (commands, command_rx) = mpsc::unbounded_channel();
    let (event_tx, mut events) = mpsc::unbounded_channel();
    let overlay = config.overlay;

    tauri::async_runtime::spawn(Agent::new(Settings::desktop(config), event_tx).run(command_rx));

    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        while let Some(event) = events.recv().await {
            if let Err(e) = on_agent_event(&app, overlay, event) {
                tracing::warn!("cannot show an event: {e}");
            }
        }
    });

    commands
}

fn on_agent_event(app: &AppHandle, overlay: config::Overlay, event: Event) -> tauri::Result<()> {
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
        // The overlay is open while Foxhole runs. If it does not open, Custos shows desktop notifications.
        Event::GameRunning(true) if overlay.enabled => app.run_on_main_thread(move || {
            if let Err(e) = overlay::open(overlay.anchor) {
                tracing::warn!("cannot open the overlay: {e}. Custos shows desktop notifications.");
            }
        })?,
        Event::GameRunning(_) => app.run_on_main_thread(overlay::close)?,
        // A message goes to the overlay when it is open, else to a desktop notification.
        Event::Notify(n) => {
            let app = app.clone();
            app.clone().run_on_main_thread(move || {
                if let Some(n) = overlay::put(n) {
                    notify(&app, &n.title, &n.body);
                }
            })?;
        }
        Event::Expire(reference) => app.run_on_main_thread(move || overlay::expire(&reference))?,
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
