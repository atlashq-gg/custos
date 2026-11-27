//! The overlay: the [`Lines`] of the messages in a window above the game.
//!
//! On Linux, the window is a native GTK window on the overlay layer of the layer-shell protocol. On
//! GNOME, which has no layer-shell protocol, it is an X11 window above the other windows. The defect
//! test of the transparent webview failed on Linux (#16), so the overlay does not use it there. On
//! Windows, the window is a transparent webview with the page `dist/overlay.html`. The two use the
//! style in `dist/overlay.css`.
//!
//! The lines of the query (see [`crate::ask`]) have their own area at the edge of the anchor, so that
//! the lines of the events do not move them. The user types a question at the prompt: then the
//! overlay takes the keyboard, and holds the new messages until the user sends the question or
//! cancels it. The prompt also closes when the overlay loses the keyboard to another window, and
//! after [`query::INPUT_TIMEOUT`] with no key. Then the typed text stays for the next prompt.
//!
//! Call each function on the main thread.

use std::cell::RefCell;
use std::time::Instant;

use custos_core::agent::Command;
use custos_core::config::Overlay as Settings;
use custos_core::lines::{ERROR_PROMPT, Lines, Notification, PROMPT, Reference};
use custos_core::query::{self, Entry, Key, Line, Outcome, Tone};
use tauri::{AppHandle, Manager};

use crate::Tray;

/// The number of seconds between two searches for the monitor of the game.
const PLACE_INTERVAL: u32 = 5;

struct Overlay {
    view: platform::View,
    lines: Lines,
    /// The lines of the query that show.
    query: Vec<Line>,
    settings: Settings,
    /// The line that the user types at the prompt. `None` while the game has the keyboard.
    entry: Option<Entry>,
    /// The text of a prompt that closed with no Enter or Escape. The next prompt starts with it.
    draft: String,
    /// True after the overlay got the keyboard for the open prompt. A loss of the keyboard before
    /// that time does not close the prompt.
    focused: bool,
    app: AppHandle,
    /// The number of seconds since the overlay opened.
    seconds: u32,
}

thread_local! {
    static OVERLAY: RefCell<Option<Overlay>> = const { RefCell::new(None) };
}

/// Opens the overlay, or opens it again with new settings. It shows a prompt while it has no lines.
pub fn open(app: &AppHandle, settings: Settings) -> Result<(), String> {
    close();
    let view = platform::View::open(app, settings)?;
    OVERLAY.with_borrow_mut(|overlay| {
        *overlay = Some(Overlay {
            view,
            lines: Lines::new(settings.into()),
            query: Vec::new(),
            settings,
            entry: None,
            focused: false,
            draft: String::new(),
            app: app.clone(),
            seconds: 0,
        });
    });
    render_query();
    Ok(())
}

/// Closes the overlay, if it is open.
pub fn close() {
    if let Some(overlay) = OVERLAY.take() {
        overlay.view.close();
    }
}

/// Adds the message to the overlay. Gives the notification back when the overlay is not open.
pub fn put(notification: Notification) -> Option<Notification> {
    OVERLAY.with_borrow_mut(|overlay| match overlay {
        Some(overlay) => {
            overlay.lines.put(notification, Instant::now());
            overlay.view.render(&overlay.lines);
            None
        }
        None => Some(notification),
    })
}

/// Changes the text of the message with the reference of `notification`, with no new typing.
pub fn update(notification: Notification) {
    with(|overlay| {
        if overlay.lines.update(notification, Instant::now()) {
            overlay.view.render(&overlay.lines);
        }
    });
}

/// Removes the message with this reference.
pub fn expire(reference: &Reference) {
    with(|overlay| {
        if overlay.lines.expire(reference) {
            overlay.view.render(&overlay.lines);
        }
    });
}

/// True while the overlay is open.
pub fn is_open() -> bool {
    OVERLAY.with_borrow(Option::is_some)
}

/// Starts a question at the prompt: the overlay takes the keyboard, and holds the new messages.
/// While the prompt is open, the overlay takes the keyboard again. Returns false when the overlay is
/// not open.
pub fn begin_input() -> bool {
    let open = OVERLAY.with_borrow_mut(|overlay| {
        let Some(overlay) = overlay else { return false };
        if overlay.entry.is_none() {
            let mut entry = Entry::new(Instant::now());
            entry.text = std::mem::take(&mut overlay.draft);
            overlay.view.render_input(Some(&entry.text));
            overlay.entry = Some(entry);
            overlay.focused = false;
            overlay.lines.hold();
        }
        overlay.view.take_keyboard();
        true
    });
    render_query();
    open
}

/// Applies a key while the overlay has the keyboard.
pub fn key(key: Key) {
    let waiting = crate::ask::is_waiting();
    let outcome = OVERLAY.with_borrow_mut(|overlay| {
        let overlay = overlay.as_mut()?;
        let entry = overlay.entry.as_mut()?;
        let outcome = entry.key(key, waiting, Instant::now());
        if outcome == Outcome::Continue {
            let text = entry.text.clone();
            overlay.view.render_input(Some(&text));
        }
        Some((outcome, overlay.app.clone()))
    });
    match outcome {
        Some((Outcome::Continue, _)) => {
            render_query();
        }
        Some((Outcome::Close, _)) => end_input(Close::Done),
        Some((Outcome::Send(input), app)) => {
            app.state::<Tray>().send(Command::Query(input));
            end_input(Close::Done);
        }
        None => {}
    }
}

/// The overlay got (`true`) or lost the keyboard. A loss after the overlay got it closes the prompt,
/// keeps the typed text, and leaves the keyboard to the window that took it. For example, a key of
/// another application, such as push-to-talk, can take the keyboard while the user holds it.
fn focus(focused: bool) {
    let lost = OVERLAY.with_borrow_mut(|overlay| {
        let Some(overlay) = overlay.as_mut().filter(|o| o.entry.is_some()) else {
            return false;
        };
        let lost = overlay.focused && !focused;
        overlay.focused = focused;
        lost
    });
    if lost {
        tracing::debug!("the overlay lost the keyboard: the prompt closes and keeps the text");
        end_input(Close::FocusLost);
    }
}

/// Why the prompt closes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Close {
    /// Enter or Escape. The text goes, and the game gets the keyboard back.
    Done,
    /// No key for [`query::INPUT_TIMEOUT`]. The text stays, and the game gets the keyboard back.
    Idle,
    /// Another window took the keyboard, and keeps it. The text stays.
    FocusLost,
}

/// Closes the prompt, and shows the held messages.
fn end_input(close: Close) {
    with(|overlay| {
        if let Some(entry) = overlay.entry.take() {
            overlay.draft = if close == Close::Done {
                String::new()
            } else {
                entry.text
            };
            overlay.view.release_keyboard(close != Close::FocusLost);
            overlay.view.render_input(None);
            overlay.lines.release(Instant::now());
            overlay.view.render(&overlay.lines);
        }
    });
    render_query();
}

/// Shows the current lines of the query, and the line with the key. While the user types, they are
/// the last query in full, the help after `/help`, the error of the typed line, and the line that
/// awaits the input. Returns false when the overlay is not open.
pub fn render_query() -> bool {
    OVERLAY.with_borrow_mut(|overlay| {
        let Some(overlay) = overlay else { return false };
        let query = match &overlay.entry {
            None => {
                let mut lines = crate::ask::overlay_lines(usize::from(overlay.settings.lines));
                lines.extend(crate::ask::key_hint());
                lines
            }
            Some(entry) => {
                let mut lines = crate::ask::full_lines();
                if entry.help {
                    lines.extend(query::help().into_iter().map(Line::indented));
                }
                if let Some(error) = &entry.error {
                    lines.push(Line {
                        prompt: ERROR_PROMPT,
                        text: error.clone(),
                        tone: Tone::Error,
                    });
                }
                lines.push(Line {
                    prompt: PROMPT,
                    text: query::AWAITING.into(),
                    tone: Tone::Input,
                });
                lines
            }
        };
        if query != overlay.query {
            overlay.view.render_query(&query);
            overlay.query = query;
        }
        true
    })
}

/// Call once each second: removes the stale messages, searches for the monitor of the game, and
/// updates the remaining time of the query.
fn tick() {
    let idle = OVERLAY.with_borrow(|overlay| {
        overlay
            .as_ref()
            .and_then(|o| o.entry.as_ref())
            .is_some_and(|entry| entry.is_idle(Instant::now()))
    });
    if idle {
        end_input(Close::Idle);
    }
    render_query();
    with(|overlay| {
        overlay.seconds += 1;
        let moved = overlay.seconds % PLACE_INTERVAL == 0 && overlay.view.place();
        if overlay.lines.prune(Instant::now()) || moved {
            overlay.view.render(&overlay.lines);
        }
    });
}

/// The command of the page `dist/overlay.js` on Windows: a key while the overlay has the keyboard.
#[tauri::command]
#[allow(clippy::needless_pass_by_value)] // Tauri gives the arguments by value.
pub fn overlay_key(app: AppHandle, key: String) -> Result<(), String> {
    let key = match key.as_str() {
        "Escape" => Key::Escape,
        "Backspace" => Key::Backspace,
        "Enter" => Key::Enter,
        _ => {
            let mut chars = key.chars();
            match (chars.next(), chars.next()) {
                (Some(c), None) => Key::Char(c),
                _ => return Ok(()),
            }
        }
    };
    app.run_on_main_thread(move || self::key(key))
        .map_err(|e| e.to_string())
}

fn with(f: impl FnOnce(&mut Overlay)) {
    OVERLAY.with_borrow_mut(|overlay| {
        if let Some(overlay) = overlay {
            f(overlay);
        }
    });
}

#[cfg(target_os = "linux")]
mod platform {
    use std::time::Instant;

    use custos_core::layout::{self, MARGIN, Rect, SIDE_MARGIN};
    use custos_core::lines::{self, Lines, Row};
    use custos_core::query::{Key, Line, Tone};
    use custos_core::screen;
    use gtk::prelude::*;
    use gtk::{cairo, gdk, glib, pango};
    use gtk_layer_shell::{Edge, KeyboardMode, Layer, LayerShell};
    use tauri::AppHandle;

    use super::Settings;

    /// The font of the lines, as in the CSS: the first modern monospace font that the computer has.
    const FONT: &str = "JetBrains Mono,Cascadia Mono,Fira Code,Source Code Pro,Hack,Noto Sans Mono,monospace 11";
    /// The style of the lines (GTK CSS).
    const CSS: &str = include_str!("../dist/overlay.css");
    /// The block cursor at the end of a line that types, and at the prompt while no line types.
    const CURSOR: char = '\u{2588}';
    /// The wait before Custos gives the keyboard back to the game, after the overlay released it.
    const GIVE_BACK: std::time::Duration = std::time::Duration::from_millis(50);

    pub struct View {
        window: gtk::Window,
        /// True for a layer surface, false for an X11 window.
        layer_shell: bool,
        lines: gtk::Box,
        /// The area of the query, at the edge of the anchor.
        query: gtk::Box,
        /// The prompt with the cursor that fades in and out while no line types.
        prompt: gtk::Box,
        /// The prompt and the line that the user types. While the user types, the cursor is the last
        /// character of this label, so that it follows the line when the line wraps.
        typed: gtk::Label,
        /// The cursor while the user does not type.
        cursor: gtk::Label,
        /// True while the overlay has the keyboard.
        input: bool,
        opened: Instant,
        /// The style, which applies to the screen until the overlay closes.
        style: gtk::CssProvider,
        settings: Settings,
        /// The number of characters after which a line wraps.
        max_chars: i32,
        /// The label of each line that shows now or later, with its line.
        labels: Vec<(gtk::Label, Row)>,
        /// The index of the monitor of the game, when Custos found it.
        monitor: Option<usize>,
        /// True after the first placement.
        placed: bool,
        /// Calls [`super::tick`] each second.
        ticker: glib::SourceId,
    }

    impl View {
        pub fn open(_app: &AppHandle, settings: Settings) -> Result<Self, String> {
            let anchor = settings.anchor;
            let window = gtk::Window::new(gtk::WindowType::Toplevel);
            window.set_widget_name("custos-overlay");
            let layer_shell = gtk_layer_shell::is_supported();
            if layer_shell {
                window.init_layer_shell();
                window.set_namespace("custos-overlay");
                window.set_layer(Layer::Overlay);
                // The game keeps the keyboard until the user types a question (see `take_keyboard`).
                window.set_keyboard_mode(KeyboardMode::None);
                let vertical = if anchor.is_top() { Edge::Top } else { Edge::Bottom };
                let horizontal = if anchor.is_left() { Edge::Left } else { Edge::Right };
                window.set_anchor(vertical, true);
                window.set_anchor(horizontal, true);
                window.set_layer_shell_margin(vertical, MARGIN);
                window.set_layer_shell_margin(horizontal, SIDE_MARGIN);
            } else if WidgetExt::display(&window).type_().name() == "GdkX11Display" {
                // The X11 path, for GNOME (see `restart_with_x11` in main.rs). A window of the type
                // notification, above the other windows, with no frame, no taskbar entry, and no focus.
                window.set_decorated(false);
                window.set_skip_taskbar_hint(true);
                window.set_skip_pager_hint(true);
                window.set_accept_focus(false);
                window.set_focus_on_map(false);
                window.set_resizable(false);
                window.set_type_hint(gdk::WindowTypeHint::Notification);
                window.set_keep_above(true);
                // The corner of the window that stays at its position when the lines change.
                window.set_gravity(match (anchor.is_top(), anchor.is_left()) {
                    (true, true) => gdk::Gravity::NorthWest,
                    (true, false) => gdk::Gravity::NorthEast,
                    (false, true) => gdk::Gravity::SouthWest,
                    (false, false) => gdk::Gravity::SouthEast,
                });
            } else {
                window.close();
                return Err("the desktop does not support the layer-shell protocol".into());
            }

            let screen = WidgetExt::screen(&window).ok_or("the window has no screen")?;
            make_transparent(&window, &screen);
            let style = gtk::CssProvider::new();
            style.load_from_data(CSS.as_bytes()).map_err(|e| e.to_string())?;

            let align = if anchor.is_left() {
                gtk::Align::Start
            } else {
                gtk::Align::End
            };
            let terminal = gtk::Box::new(gtk::Orientation::Vertical, 2);
            let lines = gtk::Box::new(gtk::Orientation::Vertical, 2);
            let query = gtk::Box::new(gtk::Orientation::Vertical, 2);
            let (prompt, typed, cursor) = prompt(align);
            connect_input(&window);
            if anchor.is_top() {
                terminal.add(&query);
            }
            terminal.add(&lines);
            terminal.add(&prompt);
            if !anchor.is_top() {
                terminal.add(&query);
            }
            window.add(&terminal);

            let ticker = glib::timeout_add_seconds_local(1, || {
                super::tick();
                glib::ControlFlow::Continue
            });

            gtk::StyleContext::add_provider_for_screen(&screen, &style, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);
            let mut view = Self {
                window,
                layer_shell,
                lines,
                query,
                prompt,
                typed,
                cursor,
                input: false,
                opened: Instant::now(),
                style,
                settings,
                max_chars: 80,
                labels: Vec::new(),
                monitor: None,
                placed: false,
                ticker,
            };
            view.place();
            view.window.show_all();
            view.render(&Lines::new(settings.into()));
            // The window draws a frame only when the text or the opacity of the cursor changes.
            view.window.add_tick_callback(|_, _| {
                super::with(|overlay| overlay.view.animate());
                glib::ControlFlow::Continue
            });
            Ok(view)
        }

        pub fn close(self) {
            self.ticker.remove();
            if let Some(screen) = WidgetExt::screen(&self.window) {
                gtk::StyleContext::remove_provider_for_screen(&screen, &self.style);
            }
            // The default handler of the close request destroys the window.
            self.window.close();
        }

        /// Gives the keyboard to the overlay. On the layer surface, the mode Exclusive can make a game
        /// leave fullscreen while the user types.
        pub fn take_keyboard(&mut self) {
            if self.layer_shell {
                self.window.set_keyboard_mode(KeyboardMode::Exclusive);
            } else {
                self.window.set_accept_focus(true);
                self.window.present_with_time(gtk::current_event_time());
            }
        }

        /// Releases the keyboard. With `to_game`, the game gets it back.
        pub fn release_keyboard(&mut self, to_game: bool) {
            if self.layer_shell {
                self.window.set_keyboard_mode(KeyboardMode::None);
            } else {
                self.window.set_accept_focus(false);
            }
            if to_game {
                // The compositor applies the new mode at the next commit of the surface. After it,
                // the game gets the keyboard again.
                glib::timeout_add_local_once(GIVE_BACK, || match screen::activate_game() {
                    Ok(true) => tracing::debug!("gave the keyboard back to Foxhole"),
                    Ok(false) => tracing::debug!("cannot find the window of Foxhole to give it the keyboard"),
                    Err(e) => tracing::debug!("cannot give the keyboard back to Foxhole: {e}"),
                });
            }
        }

        /// Shows the typed line at the prompt, or no line when `None`.
        pub fn render_input(&mut self, text: Option<&str>) {
            self.input = text.is_some();
            self.typed.set_max_width_chars(self.max_chars);
            self.typed.set_text(&match text {
                Some(text) => format!("{}{text}{CURSOR}", lines::PROMPT),
                None => lines::PROMPT.to_owned(),
            });
            self.cursor.set_visible(!self.input);
            self.animate();
            self.window.resize(1, 1);
        }

        /// Shows the lines of the query in their area. They do not type.
        pub fn render_query(&mut self, lines: &[Line]) {
            for child in self.query.children() {
                self.query.remove(&child);
            }
            for line in lines {
                let text = format!("{}{}", line.prompt, line.text);
                let label = self.label(text.chars().count(), line.tone);
                label.set_text(&text);
                self.query.add(&label);
            }
            self.query.show_all();
            self.window.resize(1, 1);
        }

        /// A label for a line of `chars` characters, with the alignment and the wrap of the overlay.
        fn label(&self, chars: usize, tone: Tone) -> gtk::Label {
            let label = gtk::Label::new(None);
            label.set_halign(if self.settings.anchor.is_left() {
                gtk::Align::Start
            } else {
                gtk::Align::End
            });
            label.set_xalign(0.0);
            label.set_line_wrap(true);
            // The window gets its minimum size, and the minimum width of a wrapping label is its
            // longest word. Thus the minimum width is the full line with the cursor, up to the limit.
            // The width does not change while the line types.
            let chars = i32::try_from(chars + 1).unwrap_or(i32::MAX);
            label.set_width_chars(chars.min(self.max_chars));
            label.set_max_width_chars(self.max_chars);
            // Not the class "error": a GTK theme can give it a background.
            match tone {
                Tone::Normal => {}
                Tone::Error => label.style_context().add_class("custos-error"),
                Tone::Input => label.style_context().add_class("custos-input"),
            }
            label
        }

        /// Shows the lines above the prompt.
        pub fn render(&mut self, lines: &Lines) {
            for child in self.lines.children() {
                self.lines.remove(&child);
            }
            self.labels.clear();
            for row in lines.rows(Instant::now()) {
                let tone = if row.is_error { Tone::Error } else { Tone::Normal };
                let label = self.label(row.prompt.len() + row.text.chars().count(), tone);
                // The line shows from its start (see `animate`).
                label.set_visible(false);
                self.lines.add(&label);
                self.labels.push((label, row));
            }
            self.animate();
            // The window shrinks to the size of its lines.
            self.window.resize(1, 1);
        }

        /// Shows the lines that show now, and types them. While no line types, shows the prompt, and
        /// fades its cursor in and out.
        fn animate(&mut self) {
            let now = Instant::now();
            let mut typing = false;
            let mut resized = false;
            for (label, row) in &self.labels {
                let shows = row.shows(now);
                if label.is_visible() != shows {
                    label.set_visible(shows);
                    resized = true;
                }
                typing |= row.is_typing(now);
                if shows {
                    let typed = row.typed(now);
                    let mut text: String = row.prompt.chars().chain(row.text.chars().take(typed)).collect();
                    if row.is_typing(now) {
                        text.push(CURSOR);
                    }
                    if label.text() != text {
                        label.set_text(&text);
                    }
                }
            }
            // While the user types, the prompt shows, and its cursor does not fade.
            let prompt = !typing || self.input;
            if self.prompt.is_visible() != prompt {
                self.prompt.set_visible(prompt);
                resized = true;
            }
            if resized {
                self.window.resize(1, 1);
            }
            if !self.input && !typing {
                self.cursor.set_opacity(lines::cursor_opacity(self.opened.elapsed()));
            }
        }

        /// Puts the window on the monitor of the game, and sets the offset and the line width for that
        /// monitor. When Custos cannot find the game window, the desktop selects the monitor. Returns true
        /// when the monitor changed.
        pub fn place(&mut self) -> bool {
            let display = WidgetExt::display(&self.window);
            let monitors: Vec<_> = (0..display.n_monitors()).filter_map(|i| display.monitor(i)).collect();
            let rects: Vec<_> = monitors
                .iter()
                .map(|m| {
                    let g = m.geometry();
                    Rect {
                        x: g.x(),
                        y: g.y(),
                        width: g.width(),
                        height: g.height(),
                    }
                })
                .collect();
            let found = screen::game_monitor(&rects);
            if self.placed && (found.is_none() || found == self.monitor) {
                return false;
            }
            let monitor = if let Some(i) = found {
                tracing::debug!("the overlay shows on monitor {} of {}", i + 1, monitors.len());
                if self.layer_shell {
                    self.window.set_monitor(&monitors[i]);
                }
                rects[i]
            } else {
                tracing::debug!("cannot find the monitor of Foxhole. The desktop selects the monitor.");
                let Some(&first) = rects.first() else { return false };
                first
            };
            self.monitor = found;
            self.placed = true;

            // GDK gives logical pixels, so the scale is 1.
            let anchor = self.settings.anchor;
            let area = layout::area(monitor, self.settings, 1.0);
            if self.layer_shell {
                let edge = if anchor.is_top() { Edge::Top } else { Edge::Bottom };
                self.window
                    .set_layer_shell_margin(edge, layout::offset(monitor.height, self.settings, 1.0));
            } else {
                // On X11, GDK and the X server use the same coordinates. The gravity of the window
                // makes this corner of the area its anchor corner.
                let x = if anchor.is_left() { area.x } else { area.x + area.width };
                let y = if anchor.is_top() { area.y } else { area.y + area.height };
                self.window.move_(x, y);
            }

            let font = pango::FontDescription::from_string(FONT);
            let char_width = self
                .window
                .pango_context()
                .metrics(Some(&font), None)
                .approximate_char_width()
                / pango::SCALE;
            self.max_chars = (area.width / char_width.max(1)).max(1);
            tracing::debug!(
                "a line wraps after {} characters ({} pixels for each character)",
                self.max_chars,
                char_width
            );
            true
        }
    }

    /// The prompt: its box, the label of the typed line, and the label of the cursor.
    fn prompt(align: gtk::Align) -> (gtk::Box, gtk::Label, gtk::Label) {
        let prompt = gtk::Box::new(gtk::Orientation::Horizontal, 0);
        prompt.set_halign(align);
        let typed = gtk::Label::new(Some(lines::PROMPT));
        typed.set_xalign(0.0);
        typed.set_line_wrap(true);
        typed.set_line_wrap_mode(pango::WrapMode::WordChar);
        let cursor = gtk::Label::new(Some(&CURSOR.to_string()));
        prompt.add(&typed);
        prompt.add(&cursor);
        (prompt, typed, cursor)
    }

    /// Sends the keys and the changes of the keyboard focus of the window to the prompt.
    fn connect_input(window: &gtk::Window) {
        window.connect_key_press_event(|_, event| {
            if let Some(key) = key_of(event) {
                super::key(key);
            }
            glib::Propagation::Stop
        });
        window.connect_focus_in_event(|_, _| {
            super::focus(true);
            glib::Propagation::Proceed
        });
        window.connect_focus_out_event(|_, _| {
            super::focus(false);
            glib::Propagation::Proceed
        });
    }

    /// The key of the typed line, or `None` for a key that the line does not use.
    fn key_of(event: &gdk::EventKey) -> Option<Key> {
        use gdk::keys::constants as keys;
        let keyval = event.keyval();
        match keyval {
            keys::Escape => Some(Key::Escape),
            keys::BackSpace => Some(Key::Backspace),
            keys::Return | keys::KP_Enter => Some(Key::Enter),
            _ => keyval.to_unicode().filter(|c| !c.is_control()).map(Key::Char),
        }
    }

    /// Makes the window transparent to the eye and to the pointer.
    fn make_transparent(window: &gtk::Window, screen: &gdk::Screen) {
        // An RGBA visual, and a clear background before GTK draws the lines.
        window.set_visual(screen.rgba_visual().as_ref());
        window.set_app_paintable(true);
        window.connect_draw(|_, cr| {
            cr.set_operator(cairo::Operator::Source);
            cr.set_source_rgba(0.0, 0.0, 0.0, 0.0);
            let _ = cr.paint();
            glib::Propagation::Proceed
        });

        // An empty input region: the pointer and its clicks go through the window to the game. GTK does
        // not keep it: a new surface has no input region, and when the window changes size, GTK sends
        // no region, which takes all input (WAYLAND_DEBUG showed `set_input_region(nil)`). Thus set
        // the region again at each map and after each new size.
        let pass_through = |window: &gtk::Window| window.input_shape_combine_region(Some(&cairo::Region::create()));
        pass_through(window);
        window.connect_map(pass_through);
        window.connect_size_allocate(move |window, _| pass_through(window));
    }
}

#[cfg(not(target_os = "linux"))]
mod platform {
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
    use std::time::{Duration, Instant};

    use custos_core::layout::{self, Rect};
    use custos_core::lines::{self, Lines};
    use custos_core::query::{Line, Tone};
    use serde_json::json;
    use tauri::webview::PageLoadEvent;
    use tauri::{AppHandle, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindow, WebviewWindowBuilder};

    use super::Settings;

    pub struct View {
        window: WebviewWindow,
        settings: Settings,
        /// The area of the window on the monitor, after the first placement.
        area: Option<Rect>,
        /// True until the overlay closes. The thread of the ticker stops when it is false.
        open: Arc<AtomicBool>,
    }

    impl View {
        /// Opens a hidden window. It shows when the page is ready (see [`show`]).
        pub fn open(app: &AppHandle, settings: Settings) -> Result<Self, String> {
            // A new label for each window: the old window can still exist when the overlay opens again.
            static WINDOWS: AtomicU32 = AtomicU32::new(0);
            let label = format!("overlay-{}", WINDOWS.fetch_add(1, Ordering::Relaxed));
            // The settings in #16: above the other windows, no frame, no taskbar entry, and no focus.
            let window = WebviewWindowBuilder::new(app, label, WebviewUrl::App("overlay.html".into()))
                .title("Custos overlay")
                .decorations(false)
                .shadow(false)
                .transparent(true)
                .resizable(false)
                .always_on_top(true)
                .skip_taskbar(true)
                .focused(false)
                .focusable(false)
                .visible(false)
                .on_page_load(|window, payload| {
                    if payload.event() == PageLoadEvent::Finished {
                        let _ = window.run_on_main_thread(show);
                    }
                })
                .build()
                .map_err(|e| e.to_string())?;
            window.on_window_event(|event| {
                if let tauri::WindowEvent::Focused(focused) = event {
                    super::focus(*focused);
                }
            });
            // The pointer and its clicks go through the window to the game. Set it only once: a change
            // after the window opens gives black areas on Windows 10 (tauri#15947).
            if let Err(e) = window.set_ignore_cursor_events(true) {
                let _ = window.destroy();
                return Err(e.to_string());
            }

            let open = Arc::new(AtomicBool::new(true));
            let ticking = Arc::clone(&open);
            let app = app.clone();
            std::thread::spawn(move || {
                while ticking.load(Ordering::Relaxed) {
                    std::thread::sleep(Duration::from_secs(1));
                    let _ = app.run_on_main_thread(super::tick);
                }
            });

            let mut view = Self {
                window,
                settings,
                area: None,
                open,
            };
            view.place();
            Ok(view)
        }

        pub fn close(self) {
            self.open.store(false, Ordering::Relaxed);
            if let Err(e) = self.window.destroy() {
                tracing::warn!("cannot close the overlay: {e}");
            }
        }

        /// Gives the lines to the page, which shows and types them at their times (see
        /// `dist/overlay.js`).
        pub fn render(&mut self, lines: &Lines) {
            let now = Instant::now();
            // Milliseconds from now, negative for a time in the past.
            let from_now = |t: Instant| {
                (t.saturating_duration_since(now).as_secs_f64() - now.saturating_duration_since(t).as_secs_f64())
                    * 1000.0
            };
            let rows: Vec<_> = lines
                .rows(now)
                .into_iter()
                .map(|row| {
                    json!({
                        "prompt": row.prompt,
                        "text": row.text,
                        "error": row.is_error,
                        "show": from_now(row.start),
                        "hide": row.hide_at.map(from_now),
                    })
                })
                .collect();
            let anchor = self.settings.anchor;
            let page = json!({
                "top": anchor.is_top(),
                "left": anchor.is_left(),
                "rate": lines::TYPING_RATE,
                "period": lines::CURSOR_PERIOD.as_secs_f64(),
                "rows": rows,
            });
            // JSON is valid JavaScript.
            if let Err(e) = self.window.eval(format!("render({page})")) {
                tracing::warn!("cannot show the lines in the overlay: {e}");
            }
        }

        /// Gives the keyboard to the overlay.
        pub fn take_keyboard(&mut self) {
            if let Err(e) = self.window.set_focusable(true).and_then(|()| self.window.set_focus()) {
                tracing::warn!("cannot take the keyboard for the overlay: {e}");
            }
        }

        /// Releases the keyboard. Windows has no step that gives it back to the game, so `to_game`
        /// has no effect.
        pub fn release_keyboard(&mut self, _to_game: bool) {
            if let Err(e) = self.window.set_focusable(false) {
                tracing::warn!("cannot release the keyboard of the overlay: {e}");
            }
        }

        /// Gives the typed line to the page, which shows it at the prompt and sends each key (see
        /// `dist/overlay.js`). `None` ends the input.
        pub fn render_input(&mut self, text: Option<&str>) {
            if let Err(e) = self.window.eval(format!("renderInput({})", json!(text))) {
                tracing::warn!("cannot show the typed line in the overlay: {e}");
            }
        }

        /// Gives the lines of the query to the page, which shows them in their area.
        pub fn render_query(&mut self, lines: &[Line]) {
            let rows: Vec<_> = lines
                .iter()
                .map(|line| {
                    let tone = match line.tone {
                        Tone::Normal => "",
                        Tone::Error => "custos-error",
                        Tone::Input => "custos-input",
                    };
                    json!({"prompt": line.prompt, "text": line.text, "class": tone})
                })
                .collect();
            if let Err(e) = self.window.eval(format!("renderQuery({})", json!(rows))) {
                tracing::warn!("cannot show the query in the overlay: {e}");
            }
        }

        /// Puts the window on the primary monitor. Custos cannot find the game window on Windows with a
        /// safe API. Returns true when the monitor changed.
        pub fn place(&mut self) -> bool {
            let monitor = match self.window.primary_monitor() {
                Ok(Some(monitor)) => monitor,
                Ok(None) => return false,
                Err(e) => {
                    tracing::warn!("cannot find the primary monitor: {e}");
                    return false;
                }
            };
            let (position, size) = (monitor.position(), monitor.size());
            let rect = Rect {
                x: position.x,
                y: position.y,
                width: i32::try_from(size.width).unwrap_or(i32::MAX),
                height: i32::try_from(size.height).unwrap_or(i32::MAX),
            };
            let area = layout::area(rect, self.settings, monitor.scale_factor());
            if self.area == Some(area) {
                return false;
            }
            self.area = Some(area);
            let placed = self
                .window
                .set_position(PhysicalPosition::new(area.x, area.y))
                .and_then(|()| self.window.set_size(PhysicalSize::new(area.width, area.height)));
            if let Err(e) = placed {
                tracing::warn!("cannot place the overlay: {e}");
            }
            true
        }
    }

    /// Shows the window with the current lines, when the page is ready. The first show does not take
    /// the focus from the game, because the window is not focused.
    fn show() {
        super::with(|overlay| {
            overlay.view.render(&overlay.lines);
            overlay.view.render_query(&overlay.query);
            if let Err(e) = overlay.view.window.show() {
                tracing::warn!("cannot show the overlay: {e}");
            }
        });
    }
}
