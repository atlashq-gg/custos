//! The key that opens the input window of the query, for example `Ctrl+Alt+K`.

use std::fmt;

/// The names of the modifiers in the configuration, and in the order of [`Shortcut::modifiers`].
const MODIFIERS: [&str; 4] = ["Ctrl", "Alt", "Shift", "Super"];
/// The names of the modifiers in the XDG shortcuts specification.
const PORTAL_MODIFIERS: [&str; 4] = ["CTRL", "ALT", "SHIFT", "LOGO"];

/// A key with its modifiers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Shortcut {
    /// For each of [`MODIFIERS`], true when the key needs it.
    modifiers: [bool; 4],
    /// `A` to `Z`, `0` to `9`, `F1` to `F12`, or `Space`.
    key: String,
}

/// `Ctrl+Alt+K`.
impl Default for Shortcut {
    fn default() -> Self {
        Self {
            modifiers: [true, true, false, false],
            key: "K".into(),
        }
    }
}

impl Shortcut {
    /// Reads a key such as `Ctrl+Alt+K`. The modifiers are `Ctrl`, `Alt`, `Shift`, and `Super`, in each
    /// order and case. The key needs `Ctrl`, `Alt`, or `Super`, so that the game keeps its own keys.
    pub fn parse(text: &str) -> Result<Self, String> {
        let error = || format!("not a valid key: {text}. An example is Ctrl+Alt+K.");
        let mut modifiers = [false; 4];
        let mut parts: Vec<&str> = text.split('+').map(str::trim).collect();
        let key = parts.pop().ok_or_else(error)?;
        for part in parts {
            let index = match part.to_ascii_lowercase().as_str() {
                "ctrl" | "control" => 0,
                "alt" => 1,
                "shift" => 2,
                "super" | "meta" | "logo" => 3,
                _ => return Err(error()),
            };
            modifiers[index] = true;
        }
        let key = normalize_key(key).ok_or_else(error)?;
        if !(modifiers[0] || modifiers[1] || modifiers[3]) {
            return Err(format!("the key {text} needs Ctrl, Alt, or Super."));
        }
        Ok(Self { modifiers, key })
    }

    /// The form of the XDG shortcuts specification, for the `GlobalShortcuts` portal: `CTRL+ALT+k`.
    #[must_use]
    pub fn to_portal(&self) -> String {
        let key = match self.key.as_str() {
            "Space" => "space".to_owned(),
            key if key.len() == 1 => key.to_ascii_lowercase(),
            key => key.to_owned(),
        };
        self.join(PORTAL_MODIFIERS, &key)
    }

    fn join(&self, names: [&str; 4], key: &str) -> String {
        let mut parts: Vec<&str> = names
            .iter()
            .zip(self.modifiers)
            .filter_map(|(name, on)| on.then_some(*name))
            .collect();
        parts.push(key);
        parts.join("+")
    }
}

/// The form of the configuration, also for Windows: `Ctrl+Alt+K`.
impl fmt::Display for Shortcut {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.join(MODIFIERS, &self.key))
    }
}

fn normalize_key(key: &str) -> Option<String> {
    let upper = key.to_ascii_uppercase();
    let is_function = upper
        .strip_prefix('F')
        .and_then(|n| n.parse::<u8>().ok())
        .is_some_and(|n| (1..=12).contains(&n));
    match upper.as_str() {
        "SPACE" => Some("Space".into()),
        _ if upper.len() == 1 && upper.chars().all(|c| c.is_ascii_alphanumeric()) => Some(upper),
        _ if is_function => Some(upper),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keys_are_read_and_written_in_both_forms() {
        let key = Shortcut::parse("ctrl + alt + k").unwrap();
        assert_eq!(key.to_string(), "Ctrl+Alt+K");
        assert_eq!(key.to_portal(), "CTRL+ALT+k");

        let key = Shortcut::parse("Super+Shift+F5").unwrap();
        assert_eq!(key.to_string(), "Shift+Super+F5");
        assert_eq!(key.to_portal(), "SHIFT+LOGO+F5");
        assert_eq!(Shortcut::parse("Alt+space").unwrap().to_portal(), "ALT+space");
    }

    #[test]
    fn default_is_the_default_of_the_configuration() {
        assert_eq!(Shortcut::default().to_string(), crate::config::Query::default().key);
    }

    #[test]
    fn keys_that_are_not_valid_are_refused() {
        for text in ["K", "Shift+K", "Ctrl+Hyper+K", "Ctrl+F13", "Ctrl+", "Ctrl+Enter"] {
            assert!(Shortcut::parse(text).is_err(), "{text}");
        }
    }
}
