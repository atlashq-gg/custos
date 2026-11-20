//! The overlay: the lines of the message [`Stream`] in a window above the game.
//!
//! On Linux, the window is a native GTK window on the overlay layer of the layer-shell protocol. The
//! defect test of the transparent webview failed on Linux (#16), so the overlay does not use it there.
//! Other platforms have no overlay yet, and Custos shows desktop notifications.
//!
//! Call each function on the main thread.

use custos_core::config::Anchor;
use custos_core::stream::{Notification, Reference};

pub use platform::{close, expire, open, put};

#[cfg(target_os = "linux")]
mod platform {
    use std::cell::RefCell;
    use std::time::Instant;

    use custos_core::stream::Stream;
    use gtk::prelude::*;
    use gtk::{cairo, glib};
    use gtk_layer_shell::{Edge, KeyboardMode, Layer, LayerShell};

    use super::{Anchor, Notification, Reference};

    /// The distance between the overlay and the edges of the screen, in pixels.
    const MARGIN: i32 = 16;
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
        anchor: Anchor,
        stream: Stream,
        /// Removes the stale lines each second.
        ticker: glib::SourceId,
    }

    thread_local! {
        static OVERLAY: RefCell<Option<Overlay>> = const { RefCell::new(None) };
    }

    /// Opens the overlay. It stays hidden while it has no lines.
    pub fn open(anchor: Anchor) -> Result<(), String> {
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
                anchor,
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
        fn render(&self) {
            for child in self.lines.children() {
                self.lines.remove(&child);
            }
            let align = if self.anchor.is_left() {
                gtk::Align::Start
            } else {
                gtk::Align::End
            };
            for notification in self.stream.lines() {
                let label = gtk::Label::new(Some(&notification.line()));
                label.set_halign(align);
                label.set_xalign(0.0);
                label.set_line_wrap(true);
                label.set_max_width_chars(80);
                if notification.is_error {
                    label.style_context().add_class("error");
                }
                self.lines.add(&label);
            }

            if self.stream.lines().next().is_none() {
                self.window.hide();
            } else {
                // The window shrinks to the size of its lines.
                self.window.resize(1, 1);
                self.window.show_all();
            }
        }
    }
}

#[cfg(not(target_os = "linux"))]
mod platform {
    use super::{Anchor, Notification, Reference};

    pub fn open(_anchor: Anchor) -> Result<(), String> {
        Err("this version has no overlay on this platform".into())
    }

    pub fn close() {}

    pub fn put(notification: Notification) -> Option<Notification> {
        Some(notification)
    }

    pub fn expire(_reference: &Reference) {}
}
