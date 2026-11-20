//! The overlay: the lines of the message [`Stream`] in a window above the game.
//!
//! On Linux, the window is a native GTK window on the overlay layer of the layer-shell protocol. On
//! GNOME, which has no layer-shell protocol, it is an X11 window above the other windows. The defect
//! test of the transparent webview failed on Linux (#16), so the overlay does not use it there.
//! Other platforms have no overlay yet, and Custos shows desktop notifications.
//!
//! Call each function on the main thread.

use custos_core::config::Overlay as Settings;
use custos_core::stream::{Notification, Reference};

pub use platform::{close, expire, open, put};

#[cfg(target_os = "linux")]
mod platform {
    use std::cell::RefCell;
    use std::time::Instant;

    use custos_core::screen::{self, Rect};
    use custos_core::stream::{self, Stream};
    use gtk::prelude::*;
    use gtk::{cairo, gdk, glib, pango};
    use gtk_layer_shell::{Edge, KeyboardMode, Layer, LayerShell};

    use super::{Notification, Reference, Settings};

    /// The distance from the top or bottom edge, in pixels, in addition to the offset.
    const MARGIN: i32 = 16;
    /// The distance from the left or right edge, in pixels.
    const SIDE_MARGIN: i32 = 8;
    /// A line wraps when it is wider than this share of the monitor width, in percent.
    const MAX_LINE_WIDTH_PERCENT: i32 = 15;
    /// The font of the lines, as in the CSS: the first modern monospace font that the computer has.
    const FONT: &str = "JetBrains Mono,Cascadia Mono,Fira Code,Source Code Pro,Hack,Noto Sans Mono,monospace 11";
    /// The style of the lines (GTK CSS).
    const CSS: &str = include_str!("overlay.css");
    /// The block cursor at the end of a line that types, and at the prompt while no line types.
    const CURSOR: char = '\u{2588}';
    /// The number of seconds between two searches for the monitor of the game.
    const PLACE_INTERVAL: u32 = 5;

    struct Overlay {
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
        stream: Stream,
        /// The label of each line, with its prompt and its full text.
        labels: Vec<(gtk::Label, &'static str, String)>,
        /// The index of the monitor of the game, when Custos found it.
        monitor: Option<usize>,
        /// True after the first placement.
        placed: bool,
        /// The number of seconds since the overlay opened.
        seconds: u32,
        /// Removes the stale lines, and searches for the monitor of the game.
        ticker: glib::SourceId,
    }

    thread_local! {
        static OVERLAY: RefCell<Option<Overlay>> = const { RefCell::new(None) };
    }

    /// Opens the overlay. It shows a prompt while it has no lines.
    pub fn open(settings: Settings) -> Result<(), String> {
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
        prompt.add(&gtk::Label::new(Some("> ")));
        prompt.add(&cursor);
        terminal.add(&lines);
        terminal.add(&prompt);
        window.add(&terminal);

        let ticker = glib::timeout_add_seconds_local(1, || {
            with(|overlay| {
                overlay.seconds += 1;
                let moved = overlay.seconds % PLACE_INTERVAL == 0 && overlay.place();
                if overlay.stream.prune(Instant::now()) || moved {
                    overlay.render();
                }
            });
            glib::ControlFlow::Continue
        });

        close();
        gtk::StyleContext::add_provider_for_screen(&screen, &style, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);
        let mut overlay = Overlay {
            window,
            layer_shell,
            lines,
            prompt,
            cursor,
            opened: Instant::now(),
            style,
            settings,
            max_chars: 80,
            stream: Stream::default(),
            labels: Vec::new(),
            monitor: None,
            placed: false,
            seconds: 0,
            ticker,
        };
        overlay.place();
        overlay.render();
        overlay.window.show_all();
        // The window draws a frame only when the text or the opacity of the cursor changes.
        overlay.window.add_tick_callback(|_, _| {
            with(Overlay::animate);
            glib::ControlFlow::Continue
        });
        OVERLAY.with_borrow_mut(|o| *o = Some(overlay));
        Ok(())
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

        // An empty input region: the pointer and its clicks go through the window to the game. A new
        // surface has no input region, and the layer-shell library makes a new surface, for example when
        // the overlay moves to the monitor of the game. Thus set the region again at each map.
        let pass_through = |window: &gtk::Window| window.input_shape_combine_region(Some(&cairo::Region::create()));
        pass_through(window);
        window.connect_map(pass_through);
    }

    /// Closes the overlay, if it is open.
    pub fn close() {
        if let Some(overlay) = OVERLAY.take() {
            overlay.ticker.remove();
            if let Some(screen) = WidgetExt::screen(&overlay.window) {
                gtk::StyleContext::remove_provider_for_screen(&screen, &overlay.style);
            }
            // The default handler of the close request destroys the window.
            overlay.window.close();
        }
    }

    /// Adds the line to the overlay. Gives the notification back when the overlay is not open.
    pub fn put(notification: Notification) -> Option<Notification> {
        OVERLAY.with_borrow_mut(|overlay| match overlay {
            Some(overlay) => {
                overlay.stream.put(notification, Instant::now());
                overlay.render();
                None
            }
            None => Some(notification),
        })
    }

    /// Removes the line with this reference.
    pub fn expire(reference: &Reference) {
        with(|overlay| {
            if overlay.stream.expire(reference) {
                overlay.render();
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

    impl Overlay {
        /// Shows the lines of the stream above the prompt.
        fn render(&mut self) {
            for child in self.lines.children() {
                self.lines.remove(&child);
            }
            let align = if self.settings.anchor.is_left() {
                gtk::Align::Start
            } else {
                gtk::Align::End
            };
            self.labels.clear();
            for notification in self.stream.lines() {
                let prompt = if notification.is_error { "! " } else { "> " };
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
            self.animate();
            // The window shrinks to the size of its lines.
            self.window.resize(1, 1);
        }

        /// Types the lines. While no line types, shows the prompt, and fades its cursor in and out.
        fn animate(&mut self) {
            let typing = self.type_lines();
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
        fn type_lines(&self) -> bool {
            let mut typing = false;
            for ((label, prompt, text), typed) in self.labels.iter().zip(self.stream.typed(Instant::now())) {
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
        fn place(&mut self) -> bool {
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
                &monitors[i]
            } else {
                tracing::debug!("cannot find the monitor of Foxhole. The desktop selects the monitor.");
                let Some(first) = monitors.first() else { return false };
                first
            };
            self.monitor = found;
            self.placed = true;

            // The offset is a share of the monitor height, so that it fits each screen size.
            let area = monitor.geometry();
            let anchor = self.settings.anchor;
            let offset = MARGIN + area.height() * i32::from(self.settings.offset) / 100;
            if self.layer_shell {
                let edge = if anchor.is_top() { Edge::Top } else { Edge::Bottom };
                self.window.set_layer_shell_margin(edge, offset);
            } else {
                // On X11, GDK and the X server use the same coordinates. The gravity of the window
                // makes this point its anchor corner.
                let x = if anchor.is_left() {
                    area.x() + SIDE_MARGIN
                } else {
                    area.x() + area.width() - SIDE_MARGIN
                };
                let y = if anchor.is_top() {
                    area.y() + offset
                } else {
                    area.y() + area.height() - offset
                };
                self.window.move_(x, y);
            }

            let font = pango::FontDescription::from_string(FONT);
            let char_width = self
                .window
                .pango_context()
                .metrics(Some(&font), None)
                .approximate_char_width()
                / pango::SCALE;
            self.max_chars = (monitor.geometry().width() * MAX_LINE_WIDTH_PERCENT / 100 / char_width.max(1)).max(1);
            tracing::debug!(
                "a line wraps after {} characters ({} pixels for each character)",
                self.max_chars,
                char_width
            );
            true
        }
    }
}

#[cfg(not(target_os = "linux"))]
mod platform {
    use super::{Notification, Reference, Settings};

    pub fn open(_settings: Settings) -> Result<(), String> {
        Err("this version has no overlay on this platform".into())
    }

    pub fn close() {}

    #[expect(clippy::unnecessary_wraps, reason = "the same signature as on Linux")]
    pub fn put(notification: Notification) -> Option<Notification> {
        Some(notification)
    }

    pub fn expire(_reference: &Reference) {}
}
