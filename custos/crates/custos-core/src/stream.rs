//! The message stream of the overlay: a short list of lines, as in a terminal, with the newest line at
//! the bottom. A line goes when it is stale, when a newer line has its reference, or when an event
//! expires its reference.
//!
//! The caller gives the time to each operation, so the tests need no clock.

use std::fmt;
use std::time::{Duration, Instant};

/// The maximum number of lines. A new line removes the oldest line above this limit.
pub const MAX_LINES: usize = 6;
/// The time that a line stays.
pub const LIFETIME: Duration = Duration::from_secs(20);
/// The time that an error line stays.
pub const ERROR_LIFETIME: Duration = Duration::from_secs(60);
/// The number of characters that the overlay types in each second.
pub const TYPING_RATE: u32 = 60;
/// The time of one fade in and fade out of the cursor while no line types.
pub const CURSOR_PERIOD: Duration = Duration::from_secs(2);

/// The opacity of the cursor while no line types: a slow fade in and fade out, from 0 to 1. The value
/// has 16 steps, so that the overlay draws a new frame only when the opacity changes by a step.
#[must_use]
pub fn cursor_opacity(elapsed: Duration) -> f64 {
    let phase = (elapsed.as_secs_f64() / CURSOR_PERIOD.as_secs_f64()).fract();
    let opacity = 0.5 - 0.5 * (phase * std::f64::consts::TAU).cos();
    (opacity * 16.0).round() / 16.0
}

/// The kind of a message.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Kind {
    /// The service accepted the file.
    Upload,
    /// Custos cannot send the file, or the service refused it.
    UploadError,
    /// The service is not available, or it failed.
    ServiceError,
    /// The result of a login.
    Login,
    /// Custos has no valid token. The user must sign in.
    SignInRequired,
    /// Custos cannot find or watch the save file.
    SaveFile,
    /// Custos cannot keep the tokens in the credential store.
    TokenStore,
}

impl Kind {
    fn name(self) -> &'static str {
        match self {
            Kind::Upload => "upload",
            Kind::UploadError => "upload-error",
            Kind::ServiceError => "service-error",
            Kind::Login => "login",
            Kind::SignInRequired => "sign-in-required",
            Kind::SaveFile => "save-file",
            Kind::TokenStore => "token-store",
        }
    }
}

/// The reference of a line. It comes from the content of the message, not from the position of the
/// line, a counter, or a random value. Thus the code that receives an event makes the same reference
/// again, with no stored handle.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct Reference {
    kind: Kind,
    /// The ID of the entity, when the message is about one entity.
    entity: Option<String>,
}

impl Reference {
    #[must_use]
    pub fn new(kind: Kind) -> Self {
        Self { kind, entity: None }
    }

    /// The reference of a message about one entity, for example one stockpile.
    #[must_use]
    pub fn of(kind: Kind, entity: impl Into<String>) -> Self {
        Self {
            kind,
            entity: Some(entity.into()),
        }
    }
}

impl From<Kind> for Reference {
    fn from(kind: Kind) -> Self {
        Self::new(kind)
    }
}

/// `kind` or `kind:entity`, for example `upload-error` or `stockpile:42`.
impl fmt::Display for Reference {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match &self.entity {
            Some(entity) => write!(f, "{}:{entity}", self.kind.name()),
            None => f.write_str(self.kind.name()),
        }
    }
}

/// A message for the user. The overlay shows it as a line, and the desktop shows it as a notification.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Notification {
    pub reference: Reference,
    pub title: String,
    pub body: String,
    pub is_error: bool,
}

impl Notification {
    /// The text of the overlay line. The title of an error says nothing, so the line is the body only.
    #[must_use]
    pub fn line(&self) -> String {
        if self.is_error {
            self.body.clone()
        } else {
            format!("{}. {}", self.title, self.body)
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct Line {
    notification: Notification,
    /// The time when the line arrived.
    since: Instant,
    until: Instant,
}

#[derive(Debug, Clone, Default)]
pub struct Stream {
    /// The oldest line first.
    lines: Vec<Line>,
}

impl Stream {
    /// Adds a line at the bottom. The line replaces a line with the same reference.
    pub fn put(&mut self, notification: Notification, now: Instant) {
        self.prune(now);
        self.lines
            .retain(|l| l.notification.reference != notification.reference);
        let lifetime = if notification.is_error {
            ERROR_LIFETIME
        } else {
            LIFETIME
        };
        self.lines.push(Line {
            notification,
            since: now,
            until: now + lifetime,
        });
        let excess = self.lines.len().saturating_sub(MAX_LINES);
        self.lines.drain(..excess);
    }

    /// Removes the line with this reference. Returns false when no line has it.
    pub fn expire(&mut self, reference: &Reference) -> bool {
        let count = self.lines.len();
        self.lines.retain(|l| l.notification.reference != *reference);
        self.lines.len() != count
    }

    /// Removes the stale lines. Returns false when no line was stale.
    pub fn prune(&mut self, now: Instant) -> bool {
        let count = self.lines.len();
        self.lines.retain(|l| now < l.until);
        self.lines.len() != count
    }

    /// The lines, the oldest first. Call [`Stream::prune`] before, to remove the stale lines.
    pub fn lines(&self) -> impl Iterator<Item = &Notification> {
        self.lines.iter().map(|l| &l.notification)
    }

    /// The number of typed characters of the text of each line, the oldest line first. A line starts
    /// when it arrives, but not before the line above it is complete, as on a ticker tape.
    #[must_use]
    pub fn typed(&self, now: Instant) -> Vec<usize> {
        let mut free = None;
        self.lines
            .iter()
            .map(|line| {
                let length = line.notification.line().chars().count();
                let start = free.map_or(line.since, |free: Instant| free.max(line.since));
                let millis = u64::try_from(length).unwrap_or(u64::MAX).saturating_mul(1000) / u64::from(TYPING_RATE);
                free = Some(start + Duration::from_millis(millis));
                let typed = now.saturating_duration_since(start).as_millis() * u128::from(TYPING_RATE) / 1000;
                usize::try_from(typed).unwrap_or(usize::MAX).min(length)
            })
            .collect()
    }

    /// The time when the next line becomes stale.
    #[must_use]
    pub fn next_expiry(&self) -> Option<Instant> {
        self.lines.iter().map(|l| l.until).min()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn message(reference: impl Into<Reference>, body: &str, is_error: bool) -> Notification {
        Notification {
            reference: reference.into(),
            title: "Custos".into(),
            body: body.into(),
            is_error,
        }
    }

    fn bodies(stream: &Stream) -> Vec<&str> {
        stream.lines().map(|n| n.body.as_str()).collect()
    }

    #[test]
    fn reference_has_the_kind_and_the_entity() {
        assert_eq!(Reference::new(Kind::UploadError).to_string(), "upload-error");
        assert_eq!(Reference::of(Kind::Upload, "w1").to_string(), "upload:w1");
        assert_eq!(Reference::of(Kind::Upload, "w1"), Reference::of(Kind::Upload, "w1"));
        assert_ne!(Reference::of(Kind::Upload, "w1"), Reference::new(Kind::Upload));
    }

    #[test]
    fn line_has_the_title_except_for_an_error() {
        let mut n = message(Kind::Upload, "Sent 8 KB.", false);
        n.title = "Stockpile data sent".into();
        assert_eq!(n.line(), "Stockpile data sent. Sent 8 KB.");
        n.is_error = true;
        assert_eq!(n.line(), "Sent 8 KB.");
    }

    #[test]
    fn newest_line_is_at_the_bottom_and_the_oldest_goes_above_the_limit() {
        let now = Instant::now();
        let mut stream = Stream::default();
        for i in 0..=MAX_LINES {
            stream.put(
                message(Reference::of(Kind::Upload, i.to_string()), &i.to_string(), false),
                now,
            );
        }

        assert_eq!(bodies(&stream), ["1", "2", "3", "4", "5", "6"]);
    }

    #[test]
    fn line_with_a_visible_reference_replaces_it_at_the_bottom() {
        let now = Instant::now();
        let mut stream = Stream::default();
        stream.put(message(Kind::ServiceError, "old", true), now);
        stream.put(message(Kind::Upload, "sent", false), now);
        stream.put(message(Kind::ServiceError, "new", true), now);

        assert_eq!(bodies(&stream), ["sent", "new"]);
    }

    #[test]
    fn lines_go_after_their_lifetime() {
        let now = Instant::now();
        let mut stream = Stream::default();
        stream.put(message(Kind::Upload, "sent", false), now);
        stream.put(message(Kind::ServiceError, "failed", true), now);
        assert_eq!(stream.next_expiry(), Some(now + LIFETIME));

        assert!(!stream.prune(now + LIFETIME.saturating_sub(Duration::from_secs(1))));
        assert!(stream.prune(now + LIFETIME));
        assert_eq!(bodies(&stream), ["failed"]);
        assert!(stream.prune(now + ERROR_LIFETIME));
        assert_eq!(stream.next_expiry(), None);
    }

    #[test]
    fn stale_lines_do_not_count_against_the_limit() {
        let now = Instant::now();
        let mut stream = Stream::default();
        for i in 0..MAX_LINES {
            stream.put(message(Reference::of(Kind::Upload, i.to_string()), "stale", false), now);
        }
        stream.put(message(Kind::ServiceError, "new", true), now + LIFETIME);

        assert_eq!(bodies(&stream), ["new"]);
    }

    #[test]
    fn lines_type_one_after_the_other() {
        let now = Instant::now();
        let mut stream = Stream::default();
        // 60 characters each: one second at the typing rate.
        stream.put(message(Kind::Upload, &"a".repeat(60), true), now);
        stream.put(message(Kind::ServiceError, &"b".repeat(60), true), now);

        assert_eq!(stream.typed(now), [0, 0]);
        assert_eq!(stream.typed(now + Duration::from_millis(500)), [30, 0]);
        assert_eq!(stream.typed(now + Duration::from_millis(1500)), [60, 30]);
        assert_eq!(stream.typed(now + Duration::from_secs(5)), [60, 60]);
    }

    #[test]
    fn line_that_arrives_later_starts_at_its_arrival() {
        let now = Instant::now();
        let mut stream = Stream::default();
        stream.put(message(Kind::Upload, &"a".repeat(60), true), now);
        stream.put(
            message(Kind::ServiceError, &"b".repeat(60), true),
            now + Duration::from_secs(10),
        );

        assert_eq!(stream.typed(now + Duration::from_millis(10_500)), [60, 30]);
    }

    #[test]
    fn cursor_fades_in_and_out() {
        assert!(cursor_opacity(Duration::ZERO) < f64::EPSILON);
        assert!((cursor_opacity(Duration::from_millis(500)) - 0.5).abs() < f64::EPSILON);
        assert!((cursor_opacity(Duration::from_secs(1)) - 1.0).abs() < f64::EPSILON);
        assert!(cursor_opacity(CURSOR_PERIOD) < f64::EPSILON);
    }

    #[test]
    fn expire_removes_only_the_line_with_the_reference() {
        let now = Instant::now();
        let mut stream = Stream::default();
        stream.put(message(Kind::SignInRequired, "sign in", true), now);
        stream.put(message(Kind::ServiceError, "failed", true), now);

        assert!(stream.expire(&Kind::SignInRequired.into()));
        assert!(!stream.expire(&Kind::SignInRequired.into()));
        assert_eq!(bodies(&stream), ["failed"]);
    }
}
