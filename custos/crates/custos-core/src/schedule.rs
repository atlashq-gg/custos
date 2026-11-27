//! The interval rule of spec/03-upload.md.

use std::time::{Duration, Instant};

use crate::config::Config;

/// Decides when Custos sends the next upload.
///
/// When Custos starts to watch the file, the file is stale. Custos ignores its changes during the start
/// delay. A change after it waits until the file did not change for the delay, and until the interval
/// is over.
#[derive(Debug, Clone)]
pub struct Schedule {
    configured: Duration,
    delay: Duration,
    start_delay: Duration,
    service_minimum: Duration,
    retry_after_until: Option<Instant>,
    last_upload: Option<Instant>,
    last_accepted_sha256: Option<String>,
    rejected_sha256: Option<String>,
    /// The time when Custos started to watch the file. `None` while it does not watch.
    started: Option<Instant>,
    /// True after [`Schedule::listening`] returned true for the current watch.
    announced: bool,
    /// The time of the last change after the start delay. `None` when no change waits.
    changed_at: Option<Instant>,
}

impl Schedule {
    #[must_use]
    pub fn new(config: &Config) -> Self {
        let mut schedule = Self {
            configured: Duration::ZERO,
            delay: Duration::ZERO,
            start_delay: Duration::ZERO,
            service_minimum: Duration::ZERO,
            retry_after_until: None,
            last_upload: None,
            last_accepted_sha256: None,
            rejected_sha256: None,
            started: None,
            announced: false,
            changed_at: None,
        };
        schedule.configure(config);
        schedule
    }

    /// Sets the interval and the delays of the configuration.
    pub fn configure(&mut self, config: &Config) {
        self.configured = Duration::from_secs(config.upload_interval_seconds);
        self.delay = Duration::from_secs(config.upload_delay_seconds);
        self.start_delay = Duration::from_secs(config.upload_start_delay_seconds);
    }

    /// Sets `upload_min_interval_seconds` from the discovery document.
    pub fn set_service_minimum(&mut self, minimum: Duration) {
        self.service_minimum = minimum;
    }

    /// The larger of the configured interval and the minimum of the service.
    #[must_use]
    pub fn interval(&self) -> Duration {
        self.configured.max(self.service_minimum)
    }

    /// Custos starts to watch the file. No change waits, and the start delay starts.
    pub fn start(&mut self, now: Instant) {
        self.started = Some(now);
        self.announced = false;
        self.changed_at = None;
    }

    /// Custos stops to watch the file.
    pub fn stop(&mut self) {
        self.started = None;
        self.changed_at = None;
    }

    /// Records a change of the file. Returns false when Custos ignores it: it does not watch, or the start
    /// delay is not over.
    pub fn changed(&mut self, now: Instant) -> bool {
        let listening = self.is_listening(now);
        if listening {
            self.changed_at = Some(now);
        }
        listening
    }

    /// True one time for each watch, at the first call after the start delay.
    pub fn listening(&mut self, now: Instant) -> bool {
        let first = !self.announced && self.is_listening(now);
        self.announced |= first;
        first
    }

    fn is_listening(&self, now: Instant) -> bool {
        self.started.is_some_and(|started| now >= started + self.start_delay)
    }

    /// True when a change waits, the file did not change for the delay, and the interval since the last
    /// upload and any `Retry-After` time are over.
    #[must_use]
    pub fn is_due(&self, now: Instant) -> bool {
        let settled = self.changed_at.is_some_and(|changed| now >= changed + self.delay);
        let interval_over = self.last_upload.is_none_or(|last| now >= last + self.interval());
        let retry_over = self.retry_after_until.is_none_or(|until| now >= until);
        settled && interval_over && retry_over
    }

    /// True when a file with this SHA-256 must go to the service: it is not the last accepted file, and not
    /// a file that the service rejected.
    #[must_use]
    pub fn is_new(&self, sha256: &str) -> bool {
        self.last_accepted_sha256.as_deref() != Some(sha256) && self.rejected_sha256.as_deref() != Some(sha256)
    }

    /// The change that waits needs no upload, because the service already has the file.
    pub fn discard(&mut self) {
        self.changed_at = None;
    }

    /// Records an upload attempt that reached the service.
    pub fn sent(&mut self, now: Instant) {
        self.last_upload = Some(now);
    }

    pub fn accepted(&mut self, sha256: String) {
        self.last_accepted_sha256 = Some(sha256);
        self.retry_after_until = None;
        self.changed_at = None;
    }

    /// The service refused this file (`413`, `415`, or `422`). Custos does not send it again.
    pub fn rejected(&mut self, sha256: String) {
        self.rejected_sha256 = Some(sha256);
        self.changed_at = None;
    }

    pub fn retry_after(&mut self, now: Instant, wait: Duration) {
        self.retry_after_until = Some(now + wait);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECOND: Duration = Duration::from_secs(1);
    const MINUTE: Duration = Duration::from_secs(60);

    /// A schedule with an interval of one minute, a delay of 10 seconds, and a start delay of one
    /// minute, that watches the file since `start`.
    fn watching(start: Instant) -> Schedule {
        let config: Config =
            toml::from_str("upload_interval_seconds = 60\nupload_delay_seconds = 10\nupload_start_delay_seconds = 60")
                .unwrap();
        let mut schedule = Schedule::new(&config);
        schedule.start(start);
        schedule
    }

    #[test]
    fn interval_is_the_larger_value() {
        let mut schedule = watching(Instant::now());
        assert_eq!(schedule.interval(), MINUTE);
        schedule.set_service_minimum(5 * MINUTE);
        assert_eq!(schedule.interval(), 5 * MINUTE);
        schedule.set_service_minimum(10 * SECOND);
        assert_eq!(schedule.interval(), MINUTE);
    }

    #[test]
    fn change_in_the_start_delay_is_ignored() {
        let start = Instant::now();
        let mut schedule = watching(start);
        assert!(!schedule.is_due(start));

        assert!(!schedule.changed(start + 59 * SECOND));
        assert!(!schedule.is_due(start + 2 * MINUTE));

        assert!(schedule.changed(start + MINUTE));
        assert!(schedule.is_due(start + MINUTE + 10 * SECOND));
    }

    #[test]
    fn change_without_a_watch_is_ignored() {
        let start = Instant::now();
        let mut schedule = watching(start);
        schedule.stop();
        assert!(!schedule.changed(start + 2 * MINUTE));
        assert!(!schedule.is_due(start + 3 * MINUTE));
    }

    #[test]
    fn listening_is_true_one_time_after_the_start_delay() {
        let start = Instant::now();
        let mut schedule = watching(start);
        assert!(!schedule.listening(start + 59 * SECOND));
        assert!(schedule.listening(start + MINUTE));
        assert!(!schedule.listening(start + 2 * MINUTE));

        schedule.start(start + 3 * MINUTE);
        assert!(!schedule.listening(start + 3 * MINUTE));
        assert!(schedule.listening(start + 4 * MINUTE));
    }

    #[test]
    fn each_change_starts_the_delay_again() {
        let start = Instant::now();
        let mut schedule = watching(start);
        let listening = start + MINUTE;
        schedule.changed(listening);
        schedule.changed(listening + 8 * SECOND);
        assert!(!schedule.is_due(listening + 10 * SECOND));
        assert!(schedule.is_due(listening + 18 * SECOND));
    }

    #[test]
    fn change_in_the_interval_goes_when_the_interval_is_over() {
        let start = Instant::now();
        let mut schedule = watching(start);
        let sent = start + 2 * MINUTE;
        schedule.changed(start + 110 * SECOND);
        schedule.sent(sent);
        schedule.accepted("a".into());

        schedule.changed(sent + SECOND);
        assert!(!schedule.is_due(sent + 30 * SECOND));
        assert!(schedule.is_due(sent + MINUTE));
    }

    #[test]
    fn retry_after_can_extend_the_wait() {
        let start = Instant::now();
        let mut schedule = watching(start);
        let sent = start + 2 * MINUTE;
        schedule.changed(start + 110 * SECOND);
        schedule.sent(sent);
        schedule.retry_after(sent, 3 * MINUTE);

        assert!(!schedule.is_due(sent + 2 * MINUTE));
        assert!(schedule.is_due(sent + 3 * MINUTE));
    }

    #[test]
    fn accepted_rejected_and_discarded_changes_do_not_wait() {
        let start = Instant::now();
        let later = start + 2 * MINUTE;
        let mut schedule = watching(start);
        for end in [
            |s: &mut Schedule| s.accepted("a".into()),
            |s: &mut Schedule| s.rejected("b".into()),
            Schedule::discard,
        ] {
            schedule.changed(later);
            end(&mut schedule);
            assert!(!schedule.is_due(later + MINUTE));
        }
    }

    #[test]
    fn accepted_and_rejected_files_are_not_sent_again() {
        let mut schedule = watching(Instant::now());
        assert!(schedule.is_new("a"));

        schedule.accepted("a".into());
        assert!(!schedule.is_new("a"));

        schedule.rejected("b".into());
        assert!(!schedule.is_new("b"));
        assert!(schedule.is_new("c"));
    }
}
