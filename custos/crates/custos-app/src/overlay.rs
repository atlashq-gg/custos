//! The overlay: the lines of the message [`Stream`] in a window above the game.
//!
//! On Linux, the window is a native GTK window on the overlay layer of the layer-shell protocol. On
//! GNOME, which has no layer-shell protocol, it is an X11 window above the other windows. The defect
//! test of the transparent webview failed on Linux (#16), so the overlay does not use it there. On
//! Windows, the window is a transparent webview with the page `dist/overlay.html`. The two use the
//! style in `dist/overlay.css`.
//!
//! Call each function on the main thread.

use std::cell::RefCell;
use std::time::Instant;

use custos_core::config::Overlay as Settings;
use custos_core::stream::{Notification, Reference, Stream};
use tauri::AppHandle;

/// The number of seconds between two searches for the monitor of the game.
const PLACE_INTERVAL: u32 = 5;

struct Overlay {
    view: platform::View,
    stream: Stream,
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
            stream: Stream::default(),
            seconds: 0,
        });
    });
    Ok(())
}

/// Closes the overlay, if it is open.
pub fn close() {
    if let Some(overlay) = OVERLAY.take() {
        overlay.view.close();
    }
}

/// Adds the line to the overlay. Gives the notification back when the overlay is not open.
pub fn put(notification: Notification) -> Option<Notification> {
    OVERLAY.with_borrow_mut(|overlay| match overlay {
        Some(overlay) => {
            overlay.stream.put(notification, Instant::now());
            overlay.view.render(&overlay.stream);
            None
        }
        None => Some(notification),
    })
}

/// Removes the line with this reference.
pub fn expire(reference: &Reference) {
    with(|overlay| {
        if overlay.stream.expire(reference) {
            overlay.view.render(&overlay.stream);
        }
    });
}

/// Call once each second: removes the stale lines, and searches for the monitor of the game.
fn tick() {
    with(|overlay| {
        overlay.seconds += 1;
        let moved = overlay.seconds % PLACE_INTERVAL == 0 && overlay.view.place();
        if overlay.stream.prune(Instant::now()) || moved {
            overlay.view.render(&overlay.stream);
        }
    });
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
    use custos_core::screen;
    use custos_core::stream::{self, Stream};
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

    pub struct View {
        window: gtk::Window,
        /// True for a layer surface, false for an X11 window.
        layer_shell: bool,
        lines: gtk::Box,
        /// The prompt with the cursor that fades in and out while no line types.
        prompt: gtk::Box,
        cursor: gtk::Label,
        opened: Instant,
        /// The style, which applies to the screen until the overlay closes.
        style: gtk::CssProvider,
        settings: Settings,
        /// The number of characters after which a line wraps.
        max_chars: i32,
        /// The label of each line, with its prompt and its full text.
        labels: Vec<(gtk::Label, &'static str, String)>,
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
                // The game keeps the keyboard. The mode Exclusive makes a game leave fullscreen.
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
            let prompt = gtk::Box::new(gtk::Orientation::Horizontal, 0);
            prompt.set_halign(align);
            let cursor = gtk::Label::new(Some(&CURSOR.to_string()));
            prompt.add(&gtk::Label::new(Some(stream::PROMPT)));
            prompt.add(&cursor);
            terminal.add(&lines);
            terminal.add(&prompt);
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
                prompt,
                cursor,
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
            view.render(&Stream::default());
            view.window.show_all();
            // The window draws a frame only when the text or the opacity of the cursor changes.
            view.window.add_tick_callback(|_, _| {
                super::with(|overlay| overlay.view.animate(&overlay.stream));
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

        /// Shows the lines of the stream above the prompt.
        pub fn render(&mut self, stream: &Stream) {
            for child in self.lines.children() {
                self.lines.remove(&child);
            }
            let align = if self.settings.anchor.is_left() {
                gtk::Align::Start
            } else {
                gtk::Align::End
            };
            self.labels.clear();
            for notification in stream.lines() {
                let prompt = notification.prompt();
                let text = notification.line();
                let label = gtk::Label::new(None);
                label.set_halign(align);
                label.set_xalign(0.0);
                label.set_line_wrap(true);
                // The window gets its minimum size, and the minimum width of a wrapping label is its
                // longest word. Thus the minimum width is the full line with the cursor, up to the limit.
                // The width does not change while the line types.
                let chars = i32::try_from(prompt.len() + text.chars().count() + 1).unwrap_or(i32::MAX);
                label.set_width_chars(chars.min(self.max_chars));
                label.set_max_width_chars(self.max_chars);
                if notification.is_error {
                    // Not the class "error": a GTK theme can give it a background.
                    label.style_context().add_class("custos-error");
                }
                label.show();
                self.lines.add(&label);
                self.labels.push((label, prompt, text));
            }
            self.animate(stream);
            // The window shrinks to the size of its lines.
            self.window.resize(1, 1);
        }

        /// Types the lines. While no line types, shows the prompt, and fades its cursor in and out.
        fn animate(&mut self, stream: &Stream) {
            let typing = self.type_lines(stream);
            if self.prompt.is_visible() == typing {
                self.prompt.set_visible(!typing);
                self.window.resize(1, 1);
            }
            if !typing {
                self.cursor.set_opacity(stream::cursor_opacity(self.opened.elapsed()));
            }
        }

        /// Shows the typed part of each line, with a cursor at the end of a line that types. Returns
        /// true while a line is not complete.
        fn type_lines(&self, stream: &Stream) -> bool {
            let mut typing = false;
            for ((label, prompt, text), typed) in self.labels.iter().zip(stream.typed(Instant::now())) {
                let mut shown: String = prompt.chars().chain(text.chars().take(typed)).collect();
                if typed < text.chars().count() {
                    shown.push(CURSOR);
                    typing = true;
                }
                if label.text() != shown {
                    label.set_text(&shown);
                }
            }
            typing
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
    use custos_core::stream::{self, Stream};
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

        /// Gives the lines of the stream to the page, which types them (see `dist/overlay.js`).
        pub fn render(&mut self, stream: &Stream) {
            let lines: Vec<_> = stream
                .lines()
                .zip(stream.typed(Instant::now()))
                .map(
                    |(n, typed)| json!({ "prompt": n.prompt(), "text": n.line(), "error": n.is_error, "typed": typed }),
                )
                .collect();
            let anchor = self.settings.anchor;
            let page = json!({
                "top": anchor.is_top(),
                "left": anchor.is_left(),
                "rate": stream::TYPING_RATE,
                "period": stream::CURSOR_PERIOD.as_secs_f64(),
                "lines": lines,
            });
            // JSON is valid JavaScript.
            if let Err(e) = self.window.eval(format!("render({page})")) {
                tracing::warn!("cannot show the lines in the overlay: {e}");
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
            overlay.view.render(&overlay.stream);
            if let Err(e) = overlay.view.window.show() {
                tracing::warn!("cannot show the overlay: {e}");
            }
        });
    }
}
