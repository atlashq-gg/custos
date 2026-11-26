//! The input window of the query. The key of [`crate::ask`] opens it. It shows the last query with
//! its full text, a help line, and an entry. Enter sends the line and closes the window, so that the
//! game gets the keyboard back. While a question waits and the entry is empty, `Y` accepts and `N`
//! refuses. Escape, or a click outside the window, closes it.
//!
//! On Linux, the window is a GTK window on the overlay layer of the layer-shell protocol, with the
//! keyboard. On GNOME, it is an X11 window above the other windows. On Windows, it is a webview window
//! with the page `dist/input.html`.
//!
//! Call each function on the main thread.

use std::cell::RefCell;

use tauri::AppHandle;

thread_local! {
    static INPUT: RefCell<Option<platform::View>> = const { RefCell::new(None) };
}

/// Opens the input window, or gives it the keyboard again.
pub fn open(app: &AppHandle) {
    INPUT.with_borrow_mut(|input| match input {
        Some(window) => window.present(),
        None => match platform::View::open(app) {
            Ok(window) => *input = Some(window),
            Err(e) => tracing::warn!("cannot open the input window: {e}"),
        },
    });
}

/// Closes the input window, if it is open.
pub fn close() {
    if let Some(window) = INPUT.take() {
        window.close();
    }
}

/// Shows the current query in the input window. Returns false when the window is not open.
pub fn refresh() -> bool {
    INPUT.with_borrow(|input| input.as_ref().inspect(|window| window.render()).is_some())
}

/// Closes the input window after the current event, so that no handler of the window runs after it
/// closed.
#[cfg(target_os = "linux")]
fn close_later(_app: &AppHandle) {
    gtk::glib::idle_add_local_once(close);
}

#[cfg(not(target_os = "linux"))]
fn close_later(app: &AppHandle) {
    let _ = app.run_on_main_thread(close);
}

/// The commands of the page `dist/input.html` on Windows. Tauri gives the arguments by value.
#[allow(clippy::needless_pass_by_value)]
pub mod commands {
    use tauri::AppHandle;

    #[tauri::command]
    pub fn query_submit(app: AppHandle, line: String) -> Result<(), String> {
        run(&app, move |app| {
            crate::ask::submit(app, &line)?;
            super::close();
            Ok(())
        })?
    }

    #[tauri::command]
    pub fn query_decide(app: AppHandle, key: String) -> Result<bool, String> {
        let Some(key) = key.chars().next() else {
            return Ok(false);
        };
        run(&app, move |app| {
            let decided = crate::ask::decide(app, key);
            if decided {
                super::close();
            }
            decided
        })
    }

    #[tauri::command]
    pub fn query_close(app: AppHandle) -> Result<(), String> {
        run(&app, |_| super::close())
    }

    /// Runs `f` on the main thread, and waits for its result.
    fn run<T: Send + 'static>(app: &AppHandle, f: impl FnOnce(&AppHandle) -> T + Send + 'static) -> Result<T, String> {
        let (sender, receiver) = std::sync::mpsc::channel();
        let handle = app.clone();
        app.run_on_main_thread(move || {
            let _ = sender.send(f(&handle));
        })
        .map_err(|e| e.to_string())?;
        receiver.recv().map_err(|e| e.to_string())
    }
}

#[cfg(target_os = "linux")]
mod platform {
    use custos_core::query::{Line, MAX_TEXT};
    use gtk::prelude::*;
    use gtk::{gdk, glib};
    use gtk_layer_shell::{KeyboardMode, Layer, LayerShell};
    use tauri::AppHandle;

    /// The style of the overlay, which also has the style of the input window (GTK CSS).
    const CSS: &str = include_str!("../dist/overlay.css");
    /// The width of the entry, in characters.
    const WIDTH: i32 = 60;

    pub struct View {
        window: gtk::Window,
        history: gtk::Box,
        hint: gtk::Label,
        style: gtk::CssProvider,
        /// Updates the remaining time of a question each second.
        ticker: glib::SourceId,
    }

    impl View {
        pub fn open(app: &AppHandle) -> Result<Self, String> {
            let window = gtk::Window::new(gtk::WindowType::Toplevel);
            window.set_widget_name("custos-input");
            window.set_title("Custos");
            if gtk_layer_shell::is_supported() {
                window.init_layer_shell();
                window.set_namespace("custos-input");
                window.set_layer(Layer::Overlay);
                // The window takes the keyboard until it closes. With no anchor, it is in the center.
                window.set_keyboard_mode(KeyboardMode::Exclusive);
            } else {
                // The X11 path, for GNOME: above the other windows, in the center, with no frame.
                window.set_decorated(false);
                window.set_skip_taskbar_hint(true);
                window.set_skip_pager_hint(true);
                window.set_keep_above(true);
                window.set_type_hint(gdk::WindowTypeHint::Dialog);
                window.set_position(gtk::WindowPosition::CenterAlways);
            }

            let screen = WidgetExt::screen(&window).ok_or("the window has no screen")?;
            let style = gtk::CssProvider::new();
            style.load_from_data(CSS.as_bytes()).map_err(|e| e.to_string())?;
            gtk::StyleContext::add_provider_for_screen(&screen, &style, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);

            let column = gtk::Box::new(gtk::Orientation::Vertical, 6);
            let history = gtk::Box::new(gtk::Orientation::Vertical, 2);
            let hint = gtk::Label::new(None);
            hint.set_xalign(0.0);
            hint.set_line_wrap(true);
            hint.set_max_width_chars(WIDTH);
            hint.style_context().add_class("custos-hint");
            let entry = gtk::Entry::new();
            entry.set_max_length(i32::try_from(MAX_TEXT).unwrap_or(i32::MAX));
            entry.set_width_chars(WIDTH);
            column.add(&history);
            column.add(&hint);
            column.add(&entry);
            window.add(&column);

            entry.connect_activate({
                let (app, hint) = (app.clone(), hint.clone());
                move |entry| match crate::ask::submit(&app, &entry.text()) {
                    Ok(()) => super::close_later(&app),
                    Err(e) => {
                        hint.set_text(&e);
                        hint.style_context().add_class("custos-error");
                    }
                }
            });
            // A change of the entry removes an error.
            entry.connect_changed({
                let hint = hint.clone();
                move |_| {
                    hint.style_context().remove_class("custos-error");
                    hint.set_text(&crate::ask::hint());
                }
            });
            window.connect_key_press_event({
                let (app, entry) = (app.clone(), entry.clone());
                move |_, event| {
                    let key = event.keyval();
                    let decision =
                        entry.text().is_empty() && key.to_unicode().is_some_and(|c| crate::ask::decide(&app, c));
                    if decision || key == gdk::keys::constants::Escape {
                        super::close_later(&app);
                        glib::Propagation::Stop
                    } else {
                        glib::Propagation::Proceed
                    }
                }
            });
            // A click outside the window, or the close of the desktop, closes it.
            window.connect_focus_out_event({
                let app = app.clone();
                move |_, _| {
                    super::close_later(&app);
                    glib::Propagation::Proceed
                }
            });
            window.connect_delete_event({
                let app = app.clone();
                move |_, _| {
                    super::close_later(&app);
                    glib::Propagation::Stop
                }
            });

            let ticker = glib::timeout_add_seconds_local(1, || {
                super::refresh();
                glib::ControlFlow::Continue
            });
            let view = Self {
                window,
                history,
                hint,
                style,
                ticker,
            };
            view.render();
            view.window.show_all();
            view.present();
            entry.grab_focus();
            Ok(view)
        }

        pub fn present(&self) {
            self.window.present();
        }

        pub fn close(self) {
            self.ticker.remove();
            if let Some(screen) = WidgetExt::screen(&self.window) {
                gtk::StyleContext::remove_provider_for_screen(&screen, &self.style);
            }
            // The default handler of the close request destroys the window.
            self.window.close();
        }

        /// Shows the last query and the help line.
        pub fn render(&self) {
            let lines = crate::ask::window_lines();
            let current: Vec<String> = self.history.children().iter().filter_map(label_text).collect();
            if current != lines.iter().map(text).collect::<Vec<_>>() {
                for child in self.history.children() {
                    self.history.remove(&child);
                }
                for line in &lines {
                    let label = gtk::Label::new(Some(&text(line)));
                    label.set_xalign(0.0);
                    label.set_line_wrap(true);
                    label.set_max_width_chars(WIDTH);
                    label.set_selectable(false);
                    if line.is_error {
                        label.style_context().add_class("custos-error");
                    }
                    self.history.add(&label);
                }
                self.history.show_all();
            }
            if !self.hint.style_context().has_class("custos-error") {
                self.hint.set_text(&crate::ask::hint());
            }
        }
    }

    fn text(line: &Line) -> String {
        format!("{}{}", line.prompt, line.text)
    }

    fn label_text(widget: &gtk::Widget) -> Option<String> {
        widget
            .downcast_ref::<gtk::Label>()
            .map(|label| label.text().to_string())
    }
}

#[cfg(not(target_os = "linux"))]
mod platform {
    use std::sync::atomic::{AtomicU32, Ordering};

    use serde_json::json;
    use tauri::webview::PageLoadEvent;
    use tauri::{AppHandle, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent};

    pub struct View {
        window: WebviewWindow,
    }

    impl View {
        /// Opens the window. The page asks for its content when it is ready.
        pub fn open(app: &AppHandle) -> Result<Self, String> {
            // A new label for each window: the old window can still exist when the window opens again.
            static WINDOWS: AtomicU32 = AtomicU32::new(0);
            let label = format!("input-{}", WINDOWS.fetch_add(1, Ordering::Relaxed));
            let window = WebviewWindowBuilder::new(app, label, WebviewUrl::App("input.html".into()))
                .title("Custos")
                .inner_size(600.0, 320.0)
                .center()
                .decorations(false)
                .resizable(false)
                .always_on_top(true)
                .skip_taskbar(true)
                .focused(true)
                .on_page_load(|window, payload| {
                    if payload.event() == PageLoadEvent::Finished {
                        let _ = window.run_on_main_thread(|| {
                            super::refresh();
                        });
                    }
                })
                .build()
                .map_err(|e| e.to_string())?;
            // A click outside the window closes it.
            let handle = app.clone();
            window.on_window_event(move |event| {
                if matches!(event, WindowEvent::Focused(false) | WindowEvent::CloseRequested { .. }) {
                    super::close_later(&handle);
                }
            });
            Ok(Self { window })
        }

        pub fn present(&self) {
            let _ = self.window.show();
            let _ = self.window.set_focus();
        }

        pub fn close(self) {
            if let Err(e) = self.window.destroy() {
                tracing::warn!("cannot close the input window: {e}");
            }
        }

        /// Gives the last query and the help line to the page (see `dist/input.js`).
        pub fn render(&self) {
            let lines: Vec<_> = crate::ask::window_lines()
                .iter()
                .map(|line| json!({"text": format!("{}{}", line.prompt, line.text), "error": line.is_error}))
                .collect();
            let page = json!({"lines": lines, "hint": crate::ask::hint()});
            // JSON is valid JavaScript.
            if let Err(e) = self.window.eval(format!("show({page})")) {
                tracing::warn!("cannot show the query in the input window: {e}");
            }
        }
    }
}
