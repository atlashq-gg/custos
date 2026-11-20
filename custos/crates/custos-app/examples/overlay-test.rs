//! The defect test of the overlay on Linux (M12 of issue #16).
//!
//! A transparent webview window on the overlay layer of the layer-shell protocol, in the top left corner.
//! The page `dist/overlay-test.html` keeps one line, and adds and removes a second line every few
//! seconds. Run it above the game, and examine stale pixels, the memory, and a crash with NVIDIA drivers.
//!
//! ```sh
//! cargo run -p custos-app --example overlay-test
//! ```

#[cfg(target_os = "linux")]
fn main() {
    use gtk::prelude::*;
    use gtk_layer_shell::{Edge, KeyboardMode, Layer, LayerShell};
    use tauri::{WebviewUrl, WebviewWindowBuilder};

    const WIDTH: i32 = 560;
    const HEIGHT: i32 = 120;
    const MARGIN: i32 = 16;

    tauri::Builder::default()
        .setup(|app| {
            let window = WebviewWindowBuilder::new(app, "overlay", WebviewUrl::App("overlay-test.html".into()))
                .transparent(true)
                .decorations(false)
                .shadow(false)
                .skip_taskbar(true)
                .focused(false)
                .focusable(false)
                .visible(false)
                .build()?;

            if !gtk_layer_shell::is_supported() {
                return Err("the compositor does not support the layer-shell protocol".into());
            }
            println!(
                "layer shell: library {}.{}.{}, protocol version {}",
                gtk_layer_shell::major_version(),
                gtk_layer_shell::minor_version(),
                gtk_layer_shell::micro_version(),
                gtk_layer_shell::protocol_version()
            );

            let gtk_window = window.gtk_window()?;
            gtk_window.init_layer_shell();
            gtk_window.set_namespace("custos-overlay");
            gtk_window.set_layer(Layer::Overlay);
            gtk_window.set_keyboard_mode(KeyboardMode::None);
            for edge in [Edge::Top, Edge::Left] {
                gtk_window.set_anchor(edge, true);
                gtk_window.set_layer_shell_margin(edge, MARGIN);
            }
            // A layer surface ignores the resize of tao.
            gtk_window.set_size_request(WIDTH, HEIGHT);
            gtk_window.resize(1, 1);

            // The input shape must be set after the window is realized, or the process aborts (tao#1409).
            // The compositor can create the surface again, and the shape is then gone. Thus set it on each
            // map.
            let mapped = window.clone();
            gtk_window.connect_map(move |_| {
                if let Err(e) = mapped.set_ignore_cursor_events(true) {
                    eprintln!("cannot ignore the pointer: {e}");
                }
            });
            gtk_window.show_all();
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("cannot start the overlay test");
}

#[cfg(not(target_os = "linux"))]
fn main() {
    eprintln!("The overlay test is for Linux only.");
}
