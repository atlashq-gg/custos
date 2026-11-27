//! The place, the size, and the colors of the overlay on a monitor. The same rules apply on Linux and
//! on Windows.

use crate::config::{Color, Overlay};

/// The distance from the top or bottom edge, in logical pixels, in addition to the offset.
pub const MARGIN: i32 = 16;
/// The distance from the left or right edge, in logical pixels.
pub const SIDE_MARGIN: i32 = 8;
/// A line wraps when it is wider than this share of the monitor width, in percent.
pub const MAX_LINE_WIDTH_PERCENT: i32 = 15;

/// A rectangle on the desktop, in pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

/// The distance of the overlay from its top or bottom edge: the margin, and the offset, which is a share
/// of the monitor height. `scale` is the number of pixels of the monitor in one logical pixel.
#[must_use]
pub fn offset(height: i32, settings: Overlay, scale: f64) -> i32 {
    scaled(MARGIN, scale) + height * i32::from(settings.offset) / 100
}

/// The area of the overlay on `monitor`. It is as wide as the longest line, and it goes from the offset
/// to the margin at the other edge. Its anchor corner is at the margins from the same corner of the
/// monitor.
#[must_use]
pub fn area(monitor: Rect, settings: Overlay, scale: f64) -> Rect {
    let anchor = settings.anchor;
    let (offset, margin, side) = (
        offset(monitor.height, settings, scale),
        scaled(MARGIN, scale),
        scaled(SIDE_MARGIN, scale),
    );
    let width = monitor.width * MAX_LINE_WIDTH_PERCENT / 100;
    Rect {
        x: if anchor.is_left() {
            monitor.x + side
        } else {
            monitor.x + monitor.width - side - width
        },
        y: monitor.y + if anchor.is_top() { offset } else { margin },
        width,
        height: (monitor.height - offset - margin).max(1),
    }
}

/// The colors of the configuration as CSS, after `dist/overlay.css`: the color of each kind of line,
/// with a glow of the same color, and a dark shadow that keeps the text readable on a light scene.
/// GTK on Linux and the page on Windows read it, so it uses only properties that GTK knows.
#[must_use]
pub fn style(settings: Overlay) -> String {
    let rule = |selector: &str, color: Color| {
        let Color { red, green, blue } = color;
        format!(
            "#custos-overlay label{selector} {{ color: {color}; text-shadow: 0 0 4px rgba({red}, {green}, {blue}, 0.7), \
             1px 1px 2px rgba(0, 0, 0, 0.9); }}\n"
        )
    };
    [
        rule("", settings.text_color),
        rule(".custos-error", settings.error_color),
        rule(".custos-input", settings.input_color),
    ]
    .concat()
}

#[expect(clippy::cast_possible_truncation, reason = "a margin is small")]
fn scaled(pixels: i32, scale: f64) -> i32 {
    (f64::from(pixels) * scale).round() as i32
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Anchor;

    const MONITOR: Rect = Rect {
        x: 1920,
        y: 0,
        width: 2000,
        height: 1000,
    };

    fn settings(anchor: Anchor) -> Overlay {
        Overlay {
            anchor,
            offset: 30,
            ..Overlay::default()
        }
    }

    #[test]
    fn style_has_each_color_with_its_glow() {
        let settings = Overlay {
            error_color: Color::rgb(1, 2, 3),
            ..Overlay::default()
        };
        let style = style(settings);
        assert!(style.contains("#custos-overlay label { color: #00ff41; text-shadow: 0 0 4px rgba(0, 255, 65, 0.7)"));
        assert!(style.contains("label.custos-error { color: #010203; text-shadow: 0 0 4px rgba(1, 2, 3, 0.7)"));
        assert!(style.contains("label.custos-input { color: #ffb000;"));
    }

    #[test]
    fn top_left_area_starts_at_the_offset() {
        let area = area(MONITOR, settings(Anchor::TopLeft), 1.0);
        assert_eq!(
            area,
            Rect {
                x: 1928,
                y: 316,
                width: 300,
                height: 668,
            }
        );
    }

    #[test]
    fn bottom_right_area_ends_at_the_offset() {
        let area = area(MONITOR, settings(Anchor::BottomRight), 1.0);
        assert_eq!(area.x + area.width, MONITOR.x + MONITOR.width - SIDE_MARGIN);
        assert_eq!(area.y, MARGIN);
        assert_eq!(
            area.y + area.height,
            MONITOR.height - offset(MONITOR.height, settings(Anchor::BottomRight), 1.0)
        );
    }

    #[test]
    fn margins_follow_the_scale() {
        let area = area(MONITOR, settings(Anchor::TopLeft), 1.5);
        assert_eq!((area.x, area.y), (1920 + 12, 24 + 300));
    }
}
