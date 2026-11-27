//! The lines of the overlay, as in a terminal, with the newest message at the bottom. A message stays
//! until its lifetime ends, until a newer message with its reference replaces it, or until an event
//! removes its reference. The overlay shows only the newest lines within the limits of the
//! configuration. An older line is hidden, and it comes back when a newer message goes.
//!
//! Each line is one line on the screen: a text that is wider than the overlay wraps into more lines
//! (see [`wrap`]), so that the limits apply to the height of the overlay.
//!
//! The caller gives the time to each operation, so the tests need no clock.

use std::time::{Duration, Instant};

use crate::config::Overlay;

/// The time that a message stays.
pub const LIFETIME: Duration = Duration::from_secs(20);
/// The time that an error message stays.
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

/// The kind of a message that Custos makes.
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
    /// Custos cannot get the stream of the service.
    Stream,
    /// Custos cannot send a question or an action of the query.
    Query,
}

/// The reference of a message. A message with the reference of a kept message replaces it.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub enum Reference {
    /// A message of Custos. Its kind is its reference, so the code that removes it makes the same
    /// reference again, with no stored handle.
    Local(Kind),
    /// The free text `reference` of a message on the stream of the service (spec/05-stream.md).
    Service(String),
}

impl From<Kind> for Reference {
    fn from(kind: Kind) -> Self {
        Self::Local(kind)
    }
}

/// A message for the user. The overlay shows it as lines, and the desktop shows it as a notification.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Notification {
    /// `None` for a message that no other message replaces.
    pub reference: Option<Reference>,
    pub title: String,
    pub body: String,
    pub is_error: bool,
}

/// The prompt of the first line of a message, and of the overlay while no line types.
pub const PROMPT: &str = "> ";
/// The prompt of the first line of an error message.
pub const ERROR_PROMPT: &str = "! ";
/// The start of each line of a message after the first, so that the text aligns under the first line.
pub const INDENT: &str = "  ";

impl Notification {
    /// The prompt of the first line.
    #[must_use]
    pub fn prompt(&self) -> &'static str {
        if self.is_error { ERROR_PROMPT } else { PROMPT }
    }

    /// The text of the overlay: "title. body" for a message of Custos, or the title alone when the
    /// body is empty. The title of an error says nothing, and a message of the service has only its
    /// text, so these show the body only.
    #[must_use]
    pub fn text(&self) -> String {
        if self.is_error || !matches!(self.reference, Some(Reference::Local(_))) {
            self.body.clone()
        } else if self.body.is_empty() {
            self.title.clone()
        } else {
            format!("{}. {}", self.title, self.body)
        }
    }

    /// The time that the message stays.
    fn lifetime(&self) -> Duration {
        if self.is_error { ERROR_LIFETIME } else { LIFETIME }
    }

    /// The lines of the overlay, which wrap at `width` characters and type one after the other from
    /// `start`.
    fn rows(&self, mut start: Instant, width: usize) -> Vec<Row> {
        self.lines()
            .iter()
            .flat_map(|line| wrap(line, width))
            .enumerate()
            .map(|(i, text)| {
                let row = Row {
                    prompt: if i == 0 { self.prompt() } else { INDENT },
                    text,
                    is_error: self.is_error,
                    start,
                    hide_at: None,
                };
                start = row.end();
                row
            })
            .collect()
    }

    /// The lines of the text. A line break (U+000A) starts a new line. Each other control character
    /// shows as a space (#51).
    #[must_use]
    pub fn lines(&self) -> Vec<String> {
        self.text()
            .split('\n')
            .map(|line| line.chars().map(|c| if c.is_control() { ' ' } else { c }).collect())
            .collect()
    }
}

/// Splits a line of text into the lines on the screen. Each line on the screen has at most `width`
/// characters, with its prompt or indent. A line breaks after the last space that fits, or in a word
/// that is wider than the line.
#[must_use]
pub fn wrap(text: &str, width: usize) -> Vec<String> {
    let width = width.saturating_sub(PROMPT.len()).max(1);
    let mut lines = Vec::new();
    let mut line = String::new();
    let mut count = 0;
    for word in text.split_inclusive(' ') {
        let length = word.trim_end_matches(' ').chars().count();
        if count > 0 && count + length > width {
            lines.push(line.trim_end_matches(' ').to_owned());
            line.clear();
            count = 0;
        }
        for c in word.chars() {
            if count == width && c != ' ' {
                lines.push(std::mem::take(&mut line));
                count = 0;
            }
            line.push(c);
            count += 1;
        }
    }
    lines.push(line.trim_end_matches(' ').to_owned());
    lines
}

/// The most messages and the most lines that the overlay shows. The stricter limit applies.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Limits {
    pub messages: usize,
    pub lines: usize,
}

impl From<Overlay> for Limits {
    fn from(settings: Overlay) -> Self {
        Self {
            messages: usize::from(settings.messages),
            lines: usize::from(settings.lines),
        }
    }
}

/// One line of the overlay, with its typing times.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Row {
    /// [`PROMPT`] or [`ERROR_PROMPT`] for the first line of a message, else [`INDENT`].
    pub prompt: &'static str,
    pub text: String,
    pub is_error: bool,
    /// The time when the line starts to type and shows.
    pub start: Instant,
    /// The time when newer lines push the line out of the limits. `None` while no scheduled line does.
    pub hide_at: Option<Instant>,
}

impl Row {
    /// True when the line shows at `now`.
    #[must_use]
    pub fn shows(&self, now: Instant) -> bool {
        self.start <= now && self.hide_at.is_none_or(|hide| now < hide)
    }

    /// The number of typed characters of the text at `now`.
    #[must_use]
    pub fn typed(&self, now: Instant) -> usize {
        let typed = now.saturating_duration_since(self.start).as_millis() * u128::from(TYPING_RATE) / 1000;
        usize::try_from(typed)
            .unwrap_or(usize::MAX)
            .min(self.text.chars().count())
    }

    /// True while the line types, or before it starts.
    #[must_use]
    pub fn is_typing(&self, now: Instant) -> bool {
        self.typed(now) < self.text.chars().count()
    }

    fn end(&self) -> Instant {
        self.start + typing_time(&self.text)
    }
}

/// The time to type `text`.
fn typing_time(text: &str) -> Duration {
    let length = u64::try_from(text.chars().count()).unwrap_or(u64::MAX);
    Duration::from_millis(length.saturating_mul(1000) / u64::from(TYPING_RATE))
}

#[derive(Debug, Clone)]
struct Message {
    notification: Notification,
    until: Instant,
    /// The lines with their start times, but with no `hide_at`.
    rows: Vec<Row>,
}

#[derive(Debug, Clone)]
pub struct Lines {
    limits: Limits,
    /// The characters of a line on the screen. A longer line wraps.
    width: usize,
    /// The lines on the screen that the area of the query uses. The lines of the messages use the rest.
    reserved: usize,
    /// The oldest message first.
    messages: Vec<Message>,
    /// The new messages while the user types a question, oldest first. `None` while no question is
    /// typed.
    held: Option<Vec<Notification>>,
}

impl Lines {
    #[must_use]
    pub fn new(limits: Limits) -> Self {
        Self {
            limits,
            width: usize::MAX,
            messages: Vec::new(),
            held: None,
            reserved: 0,
        }
    }

    /// The characters of a line on the screen.
    #[must_use]
    pub fn width(&self) -> usize {
        self.width
    }

    /// Sets the characters of a line on the screen. The new width applies to the next messages.
    /// Returns false when the width did not change.
    pub fn set_width(&mut self, width: usize) -> bool {
        let changed = self.width != width;
        self.width = width;
        changed
    }

    /// Gives `lines` lines of the limit to the area of the query. Returns false when the number did
    /// not change.
    pub fn reserve(&mut self, lines: usize) -> bool {
        let changed = self.reserved != lines;
        self.reserved = lines;
        changed
    }

    /// Holds the new messages, so that no line moves while the user types a question.
    pub fn hold(&mut self) {
        self.held.get_or_insert_with(Vec::new);
    }

    /// Shows the held messages, as if they arrived now. Their lifetimes start now.
    pub fn release(&mut self, now: Instant) {
        for notification in self.held.take().unwrap_or_default() {
            self.put(notification, now);
        }
    }

    /// Adds a message at the bottom, or in place of the message with the same reference. Its lines type
    /// one after the other, after the lines that still type, as on a ticker tape.
    pub fn put(&mut self, notification: Notification, now: Instant) {
        if let Some(held) = &mut self.held {
            // A held message with the same reference is old.
            held.retain(|n| n.reference.is_none() || n.reference != notification.reference);
            return held.push(notification);
        }
        self.prune(now);
        let index = notification
            .reference
            .as_ref()
            .and_then(|reference| self.position(reference));
        if let Some(index) = index {
            self.messages[index].rows.clear();
        }

        let start = self
            .messages
            .iter()
            .flat_map(|m| &m.rows)
            .map(Row::end)
            .fold(now, Instant::max);
        let message = Message {
            until: now + notification.lifetime(),
            rows: notification.rows(start, self.width),
            notification,
        };
        match index {
            Some(index) => self.messages[index] = message,
            None => self.messages.push(message),
        }
    }

    /// Changes the text of the message with the reference of `notification` in place. Its lines do not
    /// type again, and its lifetime starts again. Returns false when no message has the reference.
    pub fn update(&mut self, notification: Notification, now: Instant) -> bool {
        let Some(reference) = notification.reference.clone() else {
            return false;
        };
        if let Some(held) = self
            .held
            .iter_mut()
            .flatten()
            .find(|n| n.reference.as_ref() == Some(&reference))
        {
            *held = notification;
            return true;
        }
        self.prune(now);
        let Some(message) = self
            .messages
            .iter_mut()
            .find(|m| m.notification.reference.as_ref() == Some(&reference))
        else {
            return false;
        };
        let mut rows = notification.rows(message.rows.first().map_or(now, |row| row.start), self.width);
        for (row, old) in rows.iter_mut().zip(&message.rows) {
            row.start = old.start;
        }
        *message = Message {
            until: now + notification.lifetime(),
            rows,
            notification,
        };
        true
    }

    /// Removes the message with this reference. Returns false when no message has it.
    pub fn expire(&mut self, reference: &Reference) -> bool {
        if let Some(held) = &mut self.held {
            held.retain(|n| n.reference.as_ref() != Some(reference));
        }
        let count = self.messages.len();
        self.messages
            .retain(|m| m.notification.reference.as_ref() != Some(reference));
        self.messages.len() != count
    }

    /// Removes the stale messages. Returns false when no message was stale.
    pub fn prune(&mut self, now: Instant) -> bool {
        let count = self.messages.len();
        self.messages.retain(|m| now < m.until);
        self.messages.len() != count
    }

    /// The lines that show at `now` or later, the top line first. Call [`Lines::prune`] before, to
    /// remove the stale messages. The result is correct until the next change of the messages.
    #[must_use]
    pub fn rows(&self, now: Instant) -> Vec<Row> {
        let all: Vec<(usize, &Row)> = self
            .messages
            .iter()
            .enumerate()
            .flat_map(|(m, message)| message.rows.iter().map(move |row| (m, row)))
            .collect();
        let mut starts: Vec<Instant> = all.iter().map(|(_, row)| row.start).filter(|&t| t > now).collect();
        starts.sort_unstable();
        starts.dedup();

        let shown_now = self.shown(&all, now);
        all.iter()
            .enumerate()
            .filter(|(i, (_, row))| shown_now[*i] || row.start > now)
            .filter_map(|(i, (_, row))| {
                // The first time after the start of the line when the line is not in the limits.
                let mut times = starts.iter().filter(|&&t| t >= row.start.max(now));
                let hide_at = times.find(|&&t| !self.shown(&all, t)[i]).copied();
                (hide_at != Some(row.start)).then(|| Row {
                    hide_at,
                    ..(*row).clone()
                })
            })
            .collect()
    }

    /// For each line, true when it shows at `at`: it started, and it is in the limits, counted from the
    /// bottom line up. A line of a newer message pushes the older lines up and out.
    fn shown(&self, all: &[(usize, &Row)], at: Instant) -> Vec<bool> {
        let mut shown = vec![false; all.len()];
        let (mut lines, mut messages, mut last) = (0, 0, None);
        for (i, (m, row)) in all.iter().enumerate().rev() {
            if row.start > at {
                continue;
            }
            let new_message = last != Some(*m);
            if lines + 1 > self.limits.lines.saturating_sub(self.reserved)
                || messages + usize::from(new_message) > self.limits.messages
            {
                break;
            }
            lines += 1;
            messages += usize::from(new_message);
            last = Some(*m);
            shown[i] = true;
        }
        shown
    }

    /// The time when the next message becomes stale.
    #[must_use]
    pub fn next_expiry(&self) -> Option<Instant> {
        self.messages.iter().map(|m| m.until).min()
    }

    fn position(&self, reference: &Reference) -> Option<usize> {
        self.messages
            .iter()
            .position(|m| m.notification.reference.as_ref() == Some(reference))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LIMITS: Limits = Limits { messages: 6, lines: 12 };

    fn local(kind: Kind, body: &str, is_error: bool) -> Notification {
        Notification {
            reference: Some(kind.into()),
            title: "Custos".into(),
            body: body.into(),
            is_error,
        }
    }

    fn service(reference: Option<&str>, text: &str) -> Notification {
        Notification {
            reference: reference.map(|r| Reference::Service(r.into())),
            title: "Optio".into(),
            body: text.into(),
            is_error: false,
        }
    }

    /// The texts of the lines that show at `at`.
    fn shown(lines: &Lines, at: Instant) -> Vec<String> {
        lines
            .rows(at)
            .into_iter()
            .filter(|r| r.shows(at))
            .map(|r| r.text)
            .collect()
    }

    /// A time after all typing of short texts.
    fn later(now: Instant) -> Instant {
        now + Duration::from_secs(5)
    }

    #[test]
    fn held_messages_show_after_the_release_and_an_expiry_removes_them() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        lines.put(service(Some("s:1"), "Old"), now);
        lines.hold();
        lines.put(service(Some("s:2"), "First"), now);
        lines.put(service(Some("s:2"), "Second"), now);
        lines.put(service(Some("s:3"), "Gone"), now);
        assert!(!lines.expire(&Reference::Service("s:3".into())));
        assert_eq!(shown(&lines, later(now)), ["Old"]);

        let release = now + Duration::from_secs(10);
        lines.release(release);
        assert_eq!(shown(&lines, later(release)), ["Old", "Second"]);
        // The lifetime of a held message starts at the release.
        let at = release + LIFETIME * 3 / 4;
        lines.prune(at);
        assert_eq!(shown(&lines, at), ["Second"]);
    }

    #[test]
    fn text_has_the_title_except_for_an_error_and_a_service_message() {
        let mut n = local(Kind::Upload, "Sent 8 KB.", false);
        n.title = "Stockpile data sent".into();
        assert_eq!(n.text(), "Stockpile data sent. Sent 8 KB.");
        n.is_error = true;
        assert_eq!(n.text(), "Sent 8 KB.");
        assert_eq!(service(Some("s:1"), "Depot").text(), "Depot");
        let short = local(Kind::Upload, "", false);
        assert_eq!(short.text(), short.title);
    }

    #[test]
    fn line_break_starts_a_line_and_other_control_characters_are_spaces() {
        let n = service(None, "Depot, Main\nRifle\t+2\r(7)");
        assert_eq!(n.lines(), ["Depot, Main", "Rifle +2 (7)"]);
    }

    #[test]
    fn first_line_has_the_prompt_and_the_next_lines_an_indent() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        lines.put(service(None, "Depot\nRifle +2 (7)"), now);
        lines.put(local(Kind::Stream, "failed\nagain", true), now);

        let prompts: Vec<_> = lines.rows(now).into_iter().map(|r| r.prompt).collect();
        assert_eq!(prompts, [PROMPT, INDENT, ERROR_PROMPT, INDENT]);
    }

    #[test]
    fn lines_type_one_after_the_other_and_show_when_they_start() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        // 60 characters each: one second at the typing rate.
        lines.put(service(None, &format!("{}\n{}", "a".repeat(60), "b".repeat(60))), now);
        lines.put(service(None, &"c".repeat(60)), now);

        let rows = lines.rows(now);
        let at = |ms| now + Duration::from_millis(ms);
        assert_eq!(
            rows.iter().map(|r| r.start).collect::<Vec<_>>(),
            [now, at(1000), at(2000)]
        );
        assert_eq!(rows.iter().map(|r| r.typed(at(1500))).collect::<Vec<_>>(), [60, 30, 0]);
        assert_eq!(shown(&lines, at(1500)).len(), 2);
        assert!(rows[2].is_typing(at(1500)));
        assert!(!rows[2].is_typing(at(3000)));
    }

    #[test]
    fn message_that_arrives_later_starts_at_its_arrival() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        lines.put(service(None, &"a".repeat(60)), now);
        lines.put(service(None, &"b".repeat(60)), now + Duration::from_secs(10));

        assert_eq!(
            lines.rows(now + Duration::from_secs(10))[1].start,
            now + Duration::from_secs(10)
        );
    }

    #[test]
    fn wrap_breaks_after_a_space_or_in_a_long_word() {
        // 8 characters with the prompt, so 6 of text.
        assert_eq!(wrap("", 8), [""]);
        assert_eq!(wrap("abc de", 8), ["abc de"]);
        assert_eq!(wrap("abc def", 8), ["abc", "def"]);
        assert_eq!(wrap("abc  def gh", 8), ["abc", "def gh"]);
        assert_eq!(wrap("abcdefghij k", 8), ["abcdef", "ghij k"]);
        assert_eq!(wrap("ab abcdefgh", 8), ["ab", "abcdef", "gh"]);
        assert_eq!(wrap("äöü æøå", 8), ["äöü", "æøå"]);
        assert_eq!(wrap("abc", 0), ["a", "b", "c"]);
    }

    #[test]
    fn wrapped_lines_count_for_the_limit_and_the_query_reserves_lines() {
        let now = Instant::now();
        let mut lines = Lines::new(Limits { messages: 6, lines: 4 });
        lines.set_width(8);
        lines.put(service(None, "old"), now);
        lines.put(service(None, "one two three"), now);
        let at = later(now);
        assert_eq!(shown(&lines, at), ["old", "one", "two", "three"]);
        assert_eq!(lines.rows(at)[1].prompt, PROMPT);
        assert_eq!(lines.rows(at)[2].prompt, INDENT);

        assert!(lines.reserve(2));
        assert!(!lines.reserve(2));
        assert_eq!(shown(&lines, at), ["two", "three"]);
    }

    #[test]
    fn line_limit_hides_the_oldest_lines_and_keeps_them() {
        let now = Instant::now();
        let mut lines = Lines::new(Limits { messages: 6, lines: 3 });
        lines.put(local(Kind::Login, "old", false), now);
        lines.put(service(Some("s:1"), "a\nb\nc"), now + Duration::from_secs(5));

        assert_eq!(shown(&lines, now + Duration::from_secs(4)), ["Custos. old"]);
        assert_eq!(shown(&lines, now + Duration::from_secs(10)), ["a", "b", "c"]);

        // A newer message goes, so the hidden message comes back, complete.
        assert!(lines.expire(&Reference::Service("s:1".into())));
        let at = now + Duration::from_secs(11);
        assert_eq!(shown(&lines, at), ["Custos. old"]);
        assert!(!lines.rows(at)[0].is_typing(at));
    }

    #[test]
    fn message_limit_hides_the_oldest_messages() {
        let now = Instant::now();
        let mut lines = Lines::new(Limits { messages: 2, lines: 12 });
        for text in ["1", "2", "3"] {
            lines.put(service(Some(text), text), now);
        }

        assert_eq!(shown(&lines, later(now)), ["2", "3"]);
    }

    #[test]
    fn hidden_message_that_expires_does_not_come_back() {
        let now = Instant::now();
        let mut lines = Lines::new(Limits { messages: 1, lines: 12 });
        lines.put(local(Kind::Login, "old", false), now);
        lines.put(local(Kind::ServiceError, "failed", true), now + Duration::from_secs(1));

        assert!(lines.prune(now + LIFETIME));
        assert_eq!(shown(&lines, now + LIFETIME), ["failed"]);
        assert!(lines.prune(now + ERROR_LIFETIME + Duration::from_secs(1)));
        assert_eq!(lines.next_expiry(), None);
    }

    #[test]
    fn row_knows_when_a_newer_line_pushes_it_out() {
        let now = Instant::now();
        let mut lines = Lines::new(Limits { messages: 6, lines: 2 });
        // 60 characters: each line types in one second.
        let text = ["a", "b", "c"].map(|c| c.repeat(60)).join("\n");
        lines.put(service(None, &text), now);

        let rows = lines.rows(now);
        assert_eq!(rows[0].hide_at, Some(now + Duration::from_secs(2)));
        assert_eq!(rows[1].hide_at, None);
        assert!(rows[0].shows(now) && !rows[0].shows(now + Duration::from_secs(2)));
    }

    #[test]
    fn replacement_stays_in_place_types_again_and_restarts_its_lifetime() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        lines.put(service(Some("s:1"), "old"), now);
        lines.put(service(Some("s:2"), "other"), now);
        let at = now + Duration::from_secs(10);
        lines.put(service(Some("s:1"), "new\nline"), at);

        let rows = lines.rows(at);
        assert_eq!(
            rows.iter().map(|r| r.text.as_str()).collect::<Vec<_>>(),
            ["new", "line", "other"]
        );
        assert_eq!(rows[0].typed(at), 0);
        assert_eq!(rows[2].typed(at), 5, "the message after it does not type again");
        assert!(lines.prune(now + LIFETIME), "the other message goes");
        assert_eq!(lines.next_expiry(), Some(at + LIFETIME));
    }

    #[test]
    fn update_changes_the_text_with_no_typing_and_restarts_the_lifetime() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        lines.put(local(Kind::Upload, "Wait (60 s)", false), now);
        let typed = later(now);
        assert!(lines.update(local(Kind::Upload, "Wait (59 s)", false), typed));
        let rows = lines.rows(typed);
        assert_eq!(shown(&lines, typed), ["Custos. Wait (59 s)"]);
        assert!(!rows[0].is_typing(typed));

        // The first lifetime is over, but the update started it again.
        let after_first = now + LIFETIME;
        lines.prune(after_first);
        assert_eq!(shown(&lines, after_first), ["Custos. Wait (59 s)"]);

        assert!(!lines.update(local(Kind::Login, "No such message", false), typed));
        assert!(!lines.update(service(None, "No reference"), typed));

        lines.hold();
        lines.put(service(Some("s:1"), "Held"), typed);
        assert!(lines.update(service(Some("s:1"), "Held again"), typed));
        lines.release(typed);
        assert_eq!(shown(&lines, later(typed)), ["Custos. Wait (59 s)", "Held again"]);
    }

    #[test]
    fn hidden_message_is_replaced_and_removed_by_its_reference() {
        let now = Instant::now();
        let mut lines = Lines::new(Limits { messages: 1, lines: 12 });
        lines.put(service(Some("s:1"), "old"), now);
        lines.put(service(Some("s:2"), "newer"), now);
        lines.put(service(Some("s:1"), "replaced"), now);

        assert_eq!(shown(&lines, later(now)), ["newer"]);
        assert!(lines.expire(&Reference::Service("s:2".into())));
        assert_eq!(shown(&lines, later(now)), ["replaced"]);
    }

    #[test]
    fn message_without_a_reference_replaces_nothing() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        lines.put(service(None, "a"), now);
        lines.put(service(None, "a"), now);

        assert_eq!(shown(&lines, later(now)), ["a", "a"]);
    }

    #[test]
    fn service_reference_does_not_match_a_local_reference() {
        let now = Instant::now();
        let mut lines = Lines::new(LIMITS);
        lines.put(local(Kind::Upload, "sent", false), now);

        assert!(!lines.expire(&Reference::Service("upload".into())));
        assert!(lines.expire(&Kind::Upload.into()));
    }

    #[test]
    fn cursor_fades_in_and_out() {
        assert!(cursor_opacity(Duration::ZERO) < f64::EPSILON);
        assert!((cursor_opacity(Duration::from_millis(500)) - 0.5).abs() < f64::EPSILON);
        assert!((cursor_opacity(Duration::from_secs(1)) - 1.0).abs() < f64::EPSILON);
        assert!(cursor_opacity(CURSOR_PERIOD) < f64::EPSILON);
    }
}
