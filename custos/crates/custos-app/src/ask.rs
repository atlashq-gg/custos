//! The query in the application: the key that opens the input window, and the query that the overlay
//! and the input window show. The agent keeps the query, and sends each change of it.
//!
//! Call each function on the main thread, except [`listen`].

use std::cell::RefCell;
use std::time::{Duration, Instant, SystemTime};

use custos_core::agent::Command;
use custos_core::query::{self, Action, Input, Line, Query, State};
use custos_core::shortcut::Shortcut;
use tauri::{AppHandle, Manager};

use crate::Tray;

/// The query that shows, and the settings of the query.
struct Shown {
    query: Option<Query>,
    /// The key, as the configuration has it.
    key: String,
    /// The time that the answer stays in the overlay.
    lifetime: Duration,
}

thread_local! {
    static SHOWN: RefCell<Shown> = RefCell::new(Shown {
        query: None,
        key: custos_core::config::Query::default().key,
        lifetime: custos_core::config::Query::default().answer_lifetime(),
    });
}

/// Applies the `[query]` table of the configuration.
pub fn configure(settings: &custos_core::config::Query) {
    SHOWN.with_borrow_mut(|shown| {
        shown.key = settings.shortcut().to_string();
        shown.lifetime = settings.answer_lifetime();
    });
}

/// Shows the new state of the query. Returns the text for a desktop notification when the overlay
/// and the input window are not open, and the user must read the new state.
pub fn set(query: &Query) -> Option<String> {
    let old_state = SHOWN.with_borrow_mut(|shown| {
        let old = shown
            .query
            .as_ref()
            .filter(|q| q.reference == query.reference)
            .map(|q| q.state);
        shown.query = Some(query.clone());
        old
    });
    let overlay_open = crate::overlay::render_query();
    let window_open = crate::input::refresh();
    let must_read = matches!(query.state, State::Waiting | State::Closed) && old_state != Some(query.state);
    (must_read && !overlay_open && !window_open).then(|| {
        let lines = SHOWN.with_borrow(|shown| query.lines(SystemTime::now(), &shown.key, None));
        // The question of the user is the title of the notification, not a line of its text.
        lines
            .iter()
            .skip(usize::from(query.question.is_some()))
            .map(|line| line.text.as_str())
            .collect::<Vec<_>>()
            .join("\n")
    })
}

/// The lines of the query in the overlay: while the query is open, and for the lifetime of its
/// answer. The answer has at most `max_lines` lines.
pub fn overlay_lines(max_lines: usize) -> Vec<Line> {
    SHOWN.with_borrow(|shown| match &shown.query {
        Some(query) if query.shows(Instant::now(), shown.lifetime) => {
            query.lines(SystemTime::now(), &shown.key, Some(max_lines))
        }
        _ => Vec::new(),
    })
}

/// The lines of the input window: the last query with its full text.
pub fn window_lines() -> Vec<Line> {
    SHOWN.with_borrow(|shown| {
        shown
            .query
            .as_ref()
            .map(|query| query.lines(SystemTime::now(), &shown.key, None))
            .unwrap_or_default()
    })
}

/// True while the query waits for an accept or a refusal.
pub fn is_waiting() -> bool {
    SHOWN.with_borrow(|shown| shown.query.as_ref().is_some_and(|q| q.state == State::Waiting))
}

/// The help line of the input window.
pub fn hint() -> String {
    query::hint(is_waiting())
}

/// Reads a line of the input window and sends it to the agent. The error is a text for the user. The
/// window closes after `Ok`.
pub fn submit(app: &AppHandle, line: &str) -> Result<(), String> {
    if let Some(input) = query::parse(line)? {
        app.state::<Tray>().send(Command::Query(input));
    }
    Ok(())
}

/// The key of a decision in the input window: `y` accepts, and `n` refuses. Returns true when the key
/// is a decision for the query that waits.
pub fn decide(app: &AppHandle, key: char) -> bool {
    let action = match key.to_ascii_lowercase() {
        'y' => Action::Accept,
        'n' => Action::Refuse,
        _ => return false,
    };
    if !is_waiting() {
        return false;
    }
    app.state::<Tray>().send(Command::Query(Input::Act(action)));
    true
}

/// Opens the input window when the user pushes the key. On Linux, the `GlobalShortcuts` portal of the
/// desktop gives the key. Sway has no such portal, so the menu item of the tray is the only way there.
#[cfg(target_os = "linux")]
pub fn listen(app: &AppHandle, key: &Shortcut) {
    let app = app.clone();
    let trigger = key.to_portal();
    tauri::async_runtime::spawn(async move {
        if let Err(e) = portal::listen(&app, &trigger).await {
            tracing::warn!("the desktop gives no key for the input window: {e}. Use Ask a question in the tray menu.");
        }
    });
}

#[cfg(target_os = "linux")]
mod portal {
    use ashpd::desktop::CreateSessionOptions;
    use ashpd::desktop::global_shortcuts::{BindShortcutsOptions, GlobalShortcuts, NewShortcut};
    use futures_util::StreamExt;
    use tauri::AppHandle;

    /// The ID of the shortcut in the portal.
    const ASK: &str = "ask";
    /// The ID of the application for the portal: the identifier in tauri.conf.json.
    const APP_ID: &str = "gg.atlashq.custos";

    pub async fn listen(app: &AppHandle, trigger: &str) -> ashpd::Result<()> {
        // A host application tells the portal its ID before it uses a portal. An older portal does not
        // have the registry, and then uses no ID.
        if let Err(e) = ashpd::register_host_app(APP_ID.try_into()?).await {
            tracing::debug!("cannot register the application ID with the portal: {e}");
        }
        let portal = GlobalShortcuts::new().await?;
        let session = portal.create_session(CreateSessionOptions::default()).await?;
        let shortcut = NewShortcut::new(ASK, "Ask the service a question").preferred_trigger(trigger);
        let bound = portal
            .bind_shortcuts(&session, &[shortcut], None, BindShortcutsOptions::default())
            .await?
            .response()?;
        for shortcut in bound.shortcuts() {
            tracing::info!("the key {} opens the input window", shortcut.trigger_description());
        }
        let mut activated = portal.receive_activated().await?;
        while let Some(activation) = activated.next().await {
            if activation.shortcut_id() == ASK {
                let _ = app.run_on_main_thread({
                    let app = app.clone();
                    move || crate::input::open(&app)
                });
            }
        }
        // The session stays open while the stream of signals lives.
        drop(session);
        Ok(())
    }
}

/// Opens the input window when the user pushes the key: a hot key of Windows.
#[cfg(windows)]
pub fn listen(app: &AppHandle, key: &Shortcut) {
    use tauri_plugin_global_shortcut::{GlobalShortcutExt, ShortcutState};

    let shortcuts = app.global_shortcut();
    if let Err(e) = shortcuts.unregister_all() {
        tracing::warn!("cannot remove the old key: {e}");
    }
    let result = shortcuts.on_shortcut(key.to_string().as_str(), |app, _, event| {
        if event.state == ShortcutState::Pressed {
            let handle = app.clone();
            let _ = app.run_on_main_thread(move || crate::input::open(&handle));
        }
    });
    if let Err(e) = result {
        tracing::warn!("cannot use the key {key} for the input window: {e}. Use Ask a question in the tray menu.");
    }
}

#[cfg(not(any(target_os = "linux", windows)))]
pub fn listen(_app: &AppHandle, _key: &Shortcut) {}
