//! The query in the application: the key that starts a question at the prompt of the overlay, and the
//! query that the overlay shows. The agent keeps the query, and sends each change of it.
//!
//! Call each function on the main thread, except [`listen`].

use std::cell::RefCell;
use std::time::{Duration, Instant, SystemTime};

use custos_core::lines::PROMPT;
use custos_core::query::{self, Line, Query, State, Tone};
use custos_core::shortcut::Shortcut;
use tauri::AppHandle;

/// The query that shows, and the settings of the query.
struct Shown {
    query: Option<Query>,
    /// The key, as the configuration has it.
    key: String,
    /// The time that the answer stays in the overlay.
    lifetime: Duration,
    /// True when the overlay shows the key and `/help`.
    hint: bool,
    /// True when the service has a query.
    available: bool,
}

thread_local! {
    static SHOWN: RefCell<Shown> = RefCell::new(Shown {
        query: None,
        key: custos_core::config::Query::default().key,
        lifetime: custos_core::config::Query::default().answer_lifetime(),
        hint: true,
        available: false,
    });
}

/// Applies the `[query]` table of the configuration.
pub fn configure(settings: &custos_core::config::Query) {
    SHOWN.with_borrow_mut(|shown| {
        shown.key = settings.shortcut().to_string();
        shown.lifetime = settings.answer_lifetime();
        shown.hint = settings.hint;
    });
    crate::overlay::render_query();
}

/// Sets the key that the desktop bound, which can differ from the key of the configuration.
#[cfg(target_os = "linux")]
fn set_key(key: String) {
    SHOWN.with_borrow_mut(|shown| shown.key = key);
    crate::overlay::render_query();
}

/// Sets whether the service has a query.
pub fn set_available(available: bool) {
    SHOWN.with_borrow_mut(|shown| shown.available = available);
    crate::overlay::render_query();
}

/// The amber line with the key and `/help`, while the service has a query and the configuration
/// shows it.
fn key_hint(shown: &Shown) -> Option<Line> {
    (shown.available && shown.hint).then(|| Line {
        prompt: PROMPT,
        text: query::key_hint(&shown.key),
        tone: Tone::Input,
    })
}

/// Shows the new state of the query. Returns the text for a desktop notification when the overlay is
/// not open, and the user must read the new state.
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
    let must_read = matches!(query.state, State::Waiting | State::Closed) && old_state != Some(query.state);
    (must_read && !overlay_open).then(|| {
        let lines = query.lines(SystemTime::now());
        // The question of the user is the title of the notification, not a line of its text.
        lines
            .iter()
            .skip(usize::from(query.question.is_some()))
            .map(|line| line.text.as_str())
            .collect::<Vec<_>>()
            .join("\n")
    })
}

/// The lines of the query in the overlay, while the query is open and for the lifetime of its answer,
/// and the line with the key. They use at most `max` lines of `width` characters on the screen. A
/// longer text is cut, and a line tells the user that the key shows more.
pub fn overlay_lines(width: usize, max: usize) -> Vec<Line> {
    SHOWN.with_borrow(|shown| {
        let mut lines = match &shown.query {
            Some(query) if query.shows(Instant::now(), shown.lifetime) => query.lines(SystemTime::now()),
            _ => Vec::new(),
        };
        let hint = key_hint(shown);
        let tail = usize::from(hint.is_some());
        lines.extend(hint);
        query::fit(&lines, tail, width, max, &format!("{} shows more", shown.key))
    })
}

/// The lines of the last query with its full text, while the user types a question.
pub fn full_lines() -> Vec<Line> {
    SHOWN.with_borrow(|shown| {
        shown
            .query
            .as_ref()
            .map(|query| query.lines(SystemTime::now()))
            .unwrap_or_default()
    })
}

/// True while the query waits for an accept or a refusal.
pub fn is_waiting() -> bool {
    SHOWN.with_borrow(|shown| shown.query.as_ref().is_some_and(|q| q.state == State::Waiting))
}

/// Starts a question at the prompt of the overlay. Without the overlay, the key does nothing.
pub fn start() {
    if !crate::overlay::begin_input() {
        tracing::debug!("no question: the overlay is not open");
    }
}

/// Starts a question when the user pushes the key. On Linux, the `GlobalShortcuts` portal of the
/// desktop gives the key. Sway has no such portal, so the menu item of the tray is the only way there.
#[cfg(target_os = "linux")]
pub fn listen(app: &AppHandle, key: &Shortcut) {
    let app = app.clone();
    let trigger = key.to_portal();
    tauri::async_runtime::spawn(async move {
        if let Err(e) = portal::listen(&app, &trigger).await {
            tracing::warn!("the desktop gives no key for a question: {e}. Use Ask a question in the tray menu.");
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
            let key = shortcut.trigger_description().to_owned();
            tracing::info!("the key {key} starts a question");
            if shortcut.id() == ASK && !key.is_empty() {
                let _ = app.run_on_main_thread(move || super::set_key(key));
            }
        }
        let mut activated = portal.receive_activated().await?;
        while let Some(activation) = activated.next().await {
            if activation.shortcut_id() == ASK {
                let _ = app.run_on_main_thread(super::start);
            }
        }
        // The session stays open while the stream of signals lives.
        drop(session);
        Ok(())
    }
}

/// Starts a question when the user pushes the key: a hot key of Windows.
#[cfg(windows)]
pub fn listen(app: &AppHandle, key: &Shortcut) {
    use tauri_plugin_global_shortcut::{GlobalShortcutExt, ShortcutState};

    let shortcuts = app.global_shortcut();
    if let Err(e) = shortcuts.unregister_all() {
        tracing::warn!("cannot remove the old key: {e}");
    }
    let result = shortcuts.on_shortcut(key.to_string().as_str(), |app, _, event| {
        if event.state == ShortcutState::Pressed {
            let _ = app.run_on_main_thread(start);
        }
    });
    if let Err(e) = result {
        tracing::warn!("cannot use the key {key} for a question: {e}. Use Ask a question in the tray menu.");
    }
}

#[cfg(not(any(target_os = "linux", windows)))]
pub fn listen(_app: &AppHandle, _key: &Shortcut) {}
