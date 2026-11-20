//! The interval rule of spec/03-upload.md.

use std::time::{Duration, Instant};

/// Decides when Custos sends the next upload.
#[derive(Debug, Clone)]
pub struct Schedule {
    configured: Duration,
    service_minimum: Duration,
    retry_after_until: Option<Instant>,
    last_upload: Option<Instant>,
    last_accepted_sha256: Option<String>,
    rejected_sha256: Option<String>,
}

impl Schedule {
    #[must_use]
    pub fn new(configured: Duration) -> Self {
        Self {
            configured,
            service_minimum: Duration::ZERO,
            retry_after_until: None,
            last_upload: None,
            last_accepted_sha256: None,
            rejected_sha256: None,
        }
    }

    /// Sets the interval of the configuration.
    pub fn set_configured(&mut self, configured: Duration) {
        self.configured = configured;
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

    /// True when the interval since the last upload and any `Retry-After` time are over.
    #[must_use]
    pub fn is_due(&self, now: Instant) -> bool {
        let interval_over = self.last_upload.is_none_or(|last| now >= last + self.interval());
        let retry_over = self.retry_after_until.is_none_or(|until| now >= until);
        interval_over && retry_over
    }

    /// True when a file with this SHA-256 must go to the service: it is not the last accepted file, and not
    /// a file that the service rejected.
    #[must_use]
    pub fn is_new(&self, sha256: &str) -> bool {
        self.last_accepted_sha256.as_deref() != Some(sha256) && self.rejected_sha256.as_deref() != Some(sha256)
    }

    /// Records an upload attempt that reached the service.
    pub fn sent(&mut self, now: Instant) {
        self.last_upload = Some(now);
    }

    pub fn accepted(&mut self, sha256: String) {
        self.last_accepted_sha256 = Some(sha256);
        self.retry_after_until = None;
    }

    /// The service refused this file (`413`, `415`, or `422`). Custos does not send it again.
    pub fn rejected(&mut self, sha256: String) {
        self.rejected_sha256 = Some(sha256);
    }

    pub fn retry_after(&mut self, now: Instant, wait: Duration) {
        self.retry_after_until = Some(now + wait);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const MINUTE: Duration = Duration::from_secs(60);

    #[test]
    fn interval_is_the_larger_value() {
        let mut schedule = Schedule::new(MINUTE);
        assert_eq!(schedule.interval(), MINUTE);
        schedule.set_service_minimum(5 * MINUTE);
        assert_eq!(schedule.interval(), 5 * MINUTE);
        schedule.set_service_minimum(Duration::from_secs(10));
        assert_eq!(schedule.interval(), MINUTE);
    }

    #[test]
    fn first_upload_is_due_at_once_and_the_next_after_the_interval() {
        let start = Instant::now();
        let mut schedule = Schedule::new(MINUTE);
        assert!(schedule.is_due(start));

        schedule.sent(start);
        assert!(!schedule.is_due(start + Duration::from_secs(59)));
        assert!(schedule.is_due(start + MINUTE));
    }

    #[test]
    fn retry_after_can_extend_the_wait() {
        let start = Instant::now();
        let mut schedule = Schedule::new(MINUTE);
        schedule.sent(start);
        schedule.retry_after(start, 3 * MINUTE);

        assert!(!schedule.is_due(start + 2 * MINUTE));
        assert!(schedule.is_due(start + 3 * MINUTE));
    }

    #[test]
    fn accepted_and_rejected_files_are_not_sent_again() {
        let mut schedule = Schedule::new(MINUTE);
        assert!(schedule.is_new("a"));

        schedule.accepted("a".into());
        assert!(!schedule.is_new("a"));

        schedule.rejected("b".into());
        assert!(!schedule.is_new("b"));
        assert!(schedule.is_new("c"));
    }
}
