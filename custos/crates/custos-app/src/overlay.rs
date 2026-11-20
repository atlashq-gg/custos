//! The overlay: the lines of the message [`Stream`] in a window above the game.
//!
//! On Linux, the window is a native GTK window on the overlay layer of the layer-shell protocol. The
//! defect test of the transparent webview failed on Linux (#16), so the overlay does not use it there.
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
    use custos_core::stream::Stream;
    use gtk::prelude::*;
    use gtk::{cairo, glib, pango};
    use gtk_layer_shell::{Edge, KeyboardMode, Layer, LayerShell};

    use super::{Notification, Reference, Settings};

    /// The distance between the overlay and the edges of the screen, in pixels.
    const MARGIN: i32 = 16;
    /// A line wraps when it is wider than this share of the monitor width, in percent.
    const MAX_LINE_WIDTH_PERCENT: i32 = 15;
    /// The font of the lines, as in the CSS.
    const FONT: &str = "monospace 11";
    /// The style of the lines. The name of the window limits it to the overlay.
    const CSS: &str = "
        #custos-overlay { background-color: transparent; }
        #custos-overlay label {
            font-family: monospace;
            font-size: 11pt;
            color: #f0f0f0;
            background-color: rgba(0, 0, 0, 0.6);
            border-left: 3px solid #4caf50;
            padding: 2px 8px;
            margin-bottom: 4px;
        }
        #custos-overlay label.error { border-left-color: #e53935; }
    ";

    struct Overlay {
        window: gtk::Window,
        lines: gtk::Box,
        /// The style, which applies to the screen until the overlay closes.
        style: gtk::CssProvider,
        settings: Settings,
        /// The number of characters after which a line wraps.
        max_chars: i32,
        stream: Stream,
        /// Removes the stale lines each second.
        ticker: glib::SourceId,
    }

    thread_local! {
        static OVERLAY: RefCell<Option<Overlay>> = const { RefCell::new(None) };
    }

    /// Opens the overlay. It stays hidden while it has no lines.
    pub fn open(settings: Settings) -> Result<(), String> {
        let anchor = settings.anchor;
        if !gtk_layer_shell::is_supported() {
            return Err("the desktop does not support the layer-shell protocol".into());
        }

        let window = gtk::Window::new(gtk::WindowType::Toplevel);
        window.set_widget_name("custos-overlay");
        window.init_layer_shell();
        window.set_namespace("custos-overlay");
        window.set_layer(Layer::Overlay);
        // The game keeps the keyboard. The mode Exclusive makes a game leave fullscreen.
        window.set_keyboard_mode(KeyboardMode::None);
        let vertical = if anchor.is_top() { Edge::Top } else { Edge::Bottom };
        let horizontal = if anchor.is_left() { Edge::Left } else { Edge::Right };
        for edge in [vertical, horizontal] {
            window.set_anchor(edge, true);
            window.set_layer_shell_margin(edge, MARGIN);
        }

        // A transparent window: an RGBA visual, and a clear background before GTK draws the lines.
        let screen = WidgetExt::screen(&window).ok_or("the window has no screen")?;
        window.set_visual(screen.rgba_visual().as_ref());
        window.set_app_paintable(true);
        window.connect_draw(|_, cr| {
            cr.set_operator(cairo::Operator::Source);
            cr.set_source_rgba(0.0, 0.0, 0.0, 0.0);
            let _ = cr.paint();
            glib::Propagation::Proceed
        });
        let style = gtk::CssProvider::new();
        style.load_from_data(CSS.as_bytes()).map_err(|e| e.to_string())?;

        // An empty input region: the pointer goes through the window to the game. GTK sets the region
        // again each time that it creates the surface.
        window.input_shape_combine_region(Some(&cairo::Region::create()));

        let lines = gtk::Box::new(gtk::Orientation::Vertical, 0);
        window.add(&lines);

        let ticker = glib::timeout_add_seconds_local(1, || {
            with(|overlay| {
                if overlay.stream.prune(Instant::now()) {
                    overlay.render();
                }
            });
            glib::ControlFlow::Continue
        });

        close();
        gtk::StyleContext::add_provider_for_screen(&screen, &style, gtk::STYLE_PROVIDER_PRIORITY_APPLICATION);
        OVERLAY.with_borrow_mut(|o| {
            *o = Some(Overlay {
                window,
                lines,
                style,
                settings,
                max_chars: 80,
                stream: Stream::default(),
                ticker,
            });
        });
        Ok(())
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
        /// Shows the lines of the stream, and hides the window when there are none.
        fn render(&mut self) {
            let has_lines = self.stream.lines().next().is_some();
            if has_lines && !self.window.is_visible() {
                self.place();
            }
            for child in self.lines.children() {
                self.lines.remove(&child);
            }
            let align = if self.settings.anchor.is_left() {
                gtk::Align::Start
            } else {
                gtk::Align::End
            };
            for notification in self.stream.lines() {
                let text = notification.line();
                let label = gtk::Label::new(Some(&text));
                label.set_halign(align);
                label.set_xalign(0.0);
                label.set_line_wrap(true);
                // The window gets its minimum size, and the minimum width of a wrapping label is its
                // longest word. Thus the minimum width is the text, up to the limit.
                let chars = i32::try_from(text.chars().count()).unwrap_or(i32::MAX);
                label.set_width_chars(chars.min(self.max_chars));
                label.set_max_width_chars(self.max_chars);
                if notification.is_error {
                    label.style_context().add_class("error");
                }
                self.lines.add(&label);
            }

            if has_lines {
                // The window shrinks to the size of its lines.
                self.window.resize(1, 1);
                self.window.show_all();
            } else {
                self.window.hide();
            }
        }

        /// Puts the window on the monitor of the game, and sets the offset and the line width for that
        /// monitor. When Custos cannot find the game window, the desktop selects the monitor.
        fn place(&mut self) {
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
            let monitor = if let Some(i) = screen::game_monitor(&rects) {
                tracing::debug!("the overlay shows on monitor {} of {}", i + 1, monitors.len());
                self.window.set_monitor(&monitors[i]);
                &monitors[i]
            } else {
                tracing::debug!("cannot find the monitor of Foxhole. The desktop selects the monitor.");
                let Some(first) = monitors.first() else { return };
                first
            };

            // The offset is a share of the monitor height, so that it fits each screen size.
            let height = monitor.geometry().height();
            let edge = if self.settings.anchor.is_top() {
                Edge::Top
            } else {
                Edge::Bottom
            };
            self.window
                .set_layer_shell_margin(edge, MARGIN + height * i32::from(self.settings.offset) / 100);

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
