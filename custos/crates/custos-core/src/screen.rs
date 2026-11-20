//! Finds the monitor that shows Foxhole, so that the overlay shows on the same monitor. Also decides
//! when Custos needs the X11 backend for its overlay.
//!
//! Foxhole runs in Proton as an X11 window of `XWayland`. Custos reads the window list, the class and the
//! position of the windows, and the monitors of the X server. It reads no content of a window.

use x11rb::connection::Connection;
use x11rb::protocol::randr::ConnectionExt as _;
use x11rb::protocol::xproto::{AtomEnum, ConnectionExt as _, Window};
use x11rb::rust_connection::RustConnection;

use crate::locate::APP_ID;
use crate::process::EXECUTABLE;

/// The variable that marks a process that Custos started again with the X11 backend of GDK.
pub const X11_MARKER: &str = "CUSTOS_X11_RESTART";

/// True when Custos must start again with the X11 backend of GDK, before the application starts.
///
/// GNOME on Wayland has no layer-shell protocol. Through `XWayland`, the overlay can be an X11 window
/// above the game. `env` gives the value of an environment variable. A set `GDK_BACKEND` wins, and
/// the marker prevents a second start.
pub fn needs_x11_restart(env: impl Fn(&str) -> Option<String>, overlay_enabled: bool) -> bool {
    overlay_enabled
        && env(X11_MARKER).is_none()
        && env("GDK_BACKEND").is_none()
        && env("XDG_SESSION_TYPE").as_deref() == Some("wayland")
        && env("XDG_CURRENT_DESKTOP").is_some_and(|desktop| desktop.split(':').any(|d| d.eq_ignore_ascii_case("gnome")))
}

/// A rectangle on the desktop, in pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl Rect {
    fn contains_center_of(self, other: Rect) -> bool {
        let (x, y) = (other.x + other.width / 2, other.y + other.height / 2);
        (self.x..self.x + self.width).contains(&x) && (self.y..self.y + self.height).contains(&y)
    }
}

/// The index in `monitors` of the monitor that shows the game window. `monitors` are the monitors of
/// the desktop, in any order. `None` when Custos cannot find the game window or its monitor.
#[must_use]
pub fn game_monitor(monitors: &[Rect]) -> Option<usize> {
    let (game, x_monitors) = from_x_server().inspect_err(|e| tracing::debug!("{e}")).ok()?;
    select(game?, &x_monitors, monitors)
}

/// The X server can use other coordinates than the desktop, for example with a scale factor. Thus the
/// match uses the order of the monitors, from left to right and from top to bottom.
fn select(game: Rect, x_monitors: &[Rect], monitors: &[Rect]) -> Option<usize> {
    if x_monitors.len() != monitors.len() {
        return None;
    }
    let found = x_monitors.iter().position(|m| m.contains_center_of(game))?;
    let rank = order(x_monitors).iter().position(|&i| i == found)?;
    order(monitors).get(rank).copied()
}

/// The indexes of the monitors, from left to right and from top to bottom.
fn order(monitors: &[Rect]) -> Vec<usize> {
    let mut indexes: Vec<_> = (0..monitors.len()).collect();
    indexes.sort_by_key(|&i| (monitors[i].x, monitors[i].y));
    indexes
}

/// The game window, if there is one, and the monitors of the X server.
fn from_x_server() -> Result<(Option<Rect>, Vec<Rect>), Box<dyn std::error::Error>> {
    let (conn, screen) = RustConnection::connect(None)?;
    let root = conn.setup().roots[screen].root;

    let client_list = conn.intern_atom(true, b"_NET_CLIENT_LIST")?.reply()?.atom;
    let windows = conn
        .get_property(false, root, client_list, AtomEnum::WINDOW, 0, u32::MAX)?
        .reply()?;
    let mut game = None;
    for window in windows.value32().into_iter().flatten() {
        if is_game(&conn, window)? {
            let geometry = conn.get_geometry(window)?.reply()?;
            let origin = conn.translate_coordinates(window, root, 0, 0)?.reply()?;
            game = Some(Rect {
                x: origin.dst_x.into(),
                y: origin.dst_y.into(),
                width: geometry.width.into(),
                height: geometry.height.into(),
            });
            break;
        }
    }

    let monitors = conn
        .randr_get_monitors(root, true)?
        .reply()?
        .monitors
        .iter()
        .map(|m| Rect {
            x: m.x.into(),
            y: m.y.into(),
            width: m.width.into(),
            height: m.height.into(),
        })
        .collect();
    Ok((game, monitors))
}

/// True when the class of the window is the class that Proton gives to Foxhole, or the name of the
/// executable.
fn is_game(conn: &RustConnection, window: Window) -> Result<bool, Box<dyn std::error::Error>> {
    let class = conn
        .get_property(false, window, AtomEnum::WM_CLASS, AtomEnum::STRING, 0, 256)?
        .reply()?;
    let proton_class = format!("steam_app_{APP_ID}");
    Ok(class.value.split(|&b| b == 0).any(|name| {
        name.eq_ignore_ascii_case(proton_class.as_bytes()) || name.eq_ignore_ascii_case(EXECUTABLE.as_bytes())
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn env<'a>(vars: &'a [(&'a str, &'a str)]) -> impl Fn(&str) -> Option<String> + 'a {
        move |name| vars.iter().find(|(k, _)| *k == name).map(|(_, v)| (*v).to_owned())
    }

    const GNOME: [(&str, &str); 2] = [("XDG_SESSION_TYPE", "wayland"), ("XDG_CURRENT_DESKTOP", "ubuntu:GNOME")];

    #[test]
    fn gnome_on_wayland_with_the_overlay_starts_again() {
        assert!(needs_x11_restart(env(&GNOME), true));
        assert!(!needs_x11_restart(env(&GNOME), false));
    }

    #[test]
    fn other_desktops_do_not_start_again() {
        for desktop in ["KDE", "sway"] {
            let vars = [("XDG_SESSION_TYPE", "wayland"), ("XDG_CURRENT_DESKTOP", desktop)];
            assert!(!needs_x11_restart(env(&vars), true), "{desktop}");
        }
        let x11 = [("XDG_SESSION_TYPE", "x11"), ("XDG_CURRENT_DESKTOP", "GNOME")];
        assert!(!needs_x11_restart(env(&x11), true));
    }

    #[test]
    fn marker_or_a_set_backend_prevents_a_restart() {
        for extra in [(X11_MARKER, "1"), ("GDK_BACKEND", "wayland")] {
            let vars = [GNOME[0], GNOME[1], extra];
            assert!(!needs_x11_restart(env(&vars), true), "{}", extra.0);
        }
    }

    fn rect(x: i32, y: i32, width: i32, height: i32) -> Rect {
        Rect { x, y, width, height }
    }

    #[test]
    fn same_coordinates_give_the_monitor_of_the_game() {
        let monitors = [rect(1920, 0, 2560, 1440), rect(0, 0, 1920, 1080)];
        assert_eq!(select(rect(1920, 0, 2560, 1440), &monitors, &monitors), Some(0));
        assert_eq!(select(rect(100, 100, 800, 600), &monitors, &monitors), Some(1));
    }

    #[test]
    fn scaled_coordinates_give_the_monitor_in_the_same_order() {
        // The X server scales the coordinates by 2. The desktop lists the monitors in another order.
        let x_monitors = [rect(0, 0, 3840, 2160), rect(3840, 0, 5120, 2880)];
        let desktop = [rect(1920, 0, 2560, 1440), rect(0, 0, 1920, 1080)];
        assert_eq!(select(rect(3840, 0, 5120, 2880), &x_monitors, &desktop), Some(0));
    }

    #[test]
    fn no_match_when_the_monitor_lists_differ() {
        let monitors = [rect(0, 0, 1920, 1080)];
        assert_eq!(select(rect(0, 0, 1920, 1080), &monitors, &[]), None);
        assert_eq!(select(rect(5000, 0, 10, 10), &monitors, &monitors), None);
    }
}
