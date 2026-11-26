//! The client of the stream of spec/05-stream.md: one connection, its messages, and the wait before
//! the next connection. The agent decides when to connect.

use std::time::Duration;

use reqwest::StatusCode;
use reqwest::header::{ACCEPT, CONTENT_TYPE};
use serde::Deserialize;
use tokio::sync::mpsc;
use url::Url;

use crate::lines::{Notification, Reference};
use crate::sse;

/// The most characters of a text that Custos shows.
pub const MAX_TEXT: usize = 500;
/// The most characters of the text of a query message (spec/06-query.md).
pub const MAX_QUERY_TEXT: usize = 2000;
/// The longest time with no byte from the service. The service sends a comment at least each 30
/// seconds, so a connection that is silent for longer is dead.
pub const READ_TIMEOUT: Duration = Duration::from_secs(45);

/// A message of the service. Custos ignores the members that it does not use.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct Message {
    pub kind: String,
    pub text: Option<String>,
    pub reference: Option<String>,
    /// The state of a query message.
    pub state: Option<String>,
    pub deadline: Option<String>,
    pub cause: Option<String>,
}

/// The kind of a message of a query.
pub const QUERY: &str = "query";

/// What a message does to the lines of the overlay.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Action {
    /// Show the message, in place of the message with the same reference.
    Show(Notification),
    /// Remove the message with this reference.
    Remove(Reference),
}

impl Message {
    /// The update of a query message. `None` for another kind, and for a query message that is not
    /// valid or that has a state that Custos does not know.
    #[must_use]
    pub fn query_update(&self) -> Option<crate::query::Update> {
        if self.kind != QUERY {
            return None;
        }
        crate::query::Update::new(
            self.reference.clone()?,
            self.state.as_deref()?,
            self.text.clone(),
            self.deadline.as_deref(),
            self.cause.as_deref(),
        )
    }

    /// The action of the message. A message of a kind that Custos does not know is an event. A
    /// message with no text and no reference does nothing, and a query message is not a line.
    #[must_use]
    pub fn action(self, service_name: &str) -> Option<Action> {
        if self.kind == QUERY {
            return None;
        }
        let reference = self.reference.map(Reference::Service);
        match self.text {
            Some(text) => Some(Action::Show(Notification {
                reference,
                title: service_name.to_owned(),
                body: text.chars().take(MAX_TEXT).collect(),
                is_error: self.kind == "error",
            })),
            None => reference.map(Action::Remove),
        }
    }
}

/// What a connection tells the agent, in order.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Signal {
    /// The service answered `200`. Events follow.
    Opened,
    /// The position after the last event.
    Position(String),
    Message(Message),
    /// The connection ended. It is the last signal.
    Ended(End),
}

/// Why a connection ended, with the action of spec/05-stream.md.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum End {
    /// The open stream ended, or it failed. Connect again.
    Closed(String),
    /// `401`: the challenge steps of spec/02-authentication.md.
    Unauthorized,
    /// `403`: do not connect again until the service URL or the user changes.
    Forbidden,
    /// `404`: get the discovery document again.
    NotFound,
    /// `429`: wait for this time.
    RateLimited(Duration),
    /// An error of the service or of the network before the stream opened. Connect again after the wait.
    Failed(String),
}

/// The HTTP client of the stream. It has no total timeout, because a stream stays open for a long time.
#[must_use]
pub fn client(connect_timeout: Duration) -> reqwest::Client {
    reqwest::Client::builder()
        .user_agent(concat!("Custos/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(connect_timeout)
        .read_timeout(READ_TIMEOUT)
        .build()
        .unwrap_or_default()
}

/// Opens the stream and reads it until it ends. Sends each [`Signal`] to the agent.
pub async fn run(
    http: reqwest::Client,
    endpoint: Url,
    token: String,
    position: Option<String>,
    signals: mpsc::UnboundedSender<Signal>,
) {
    let end = read(&http, endpoint, &token, position, &signals).await;
    let _ = signals.send(Signal::Ended(end));
}

async fn read(
    http: &reqwest::Client,
    endpoint: Url,
    token: &str,
    position: Option<String>,
    signals: &mpsc::UnboundedSender<Signal>,
) -> End {
    let mut request = http
        .get(endpoint)
        .bearer_auth(token)
        .header(ACCEPT, "text/event-stream");
    if let Some(position) = position {
        request = request.header("Last-Event-ID", position);
    }
    let mut response = match request.send().await {
        Ok(response) => response,
        Err(e) => return End::Failed(crate::root_cause(&e)),
    };

    match response.status() {
        StatusCode::OK => {}
        StatusCode::UNAUTHORIZED => return End::Unauthorized,
        StatusCode::FORBIDDEN => return End::Forbidden,
        StatusCode::NOT_FOUND => return End::NotFound,
        StatusCode::TOO_MANY_REQUESTS => return End::RateLimited(crate::upload::retry_after(response.headers())),
        status => return End::Failed(format!("the service answered {status}")),
    }
    let is_event_stream = response
        .headers()
        .get(CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .is_some_and(|value| value.starts_with("text/event-stream"));
    if !is_event_stream {
        return End::Failed("the response is not an event stream".into());
    }

    let _ = signals.send(Signal::Opened);
    let mut parser = sse::Parser::default();
    loop {
        match response.chunk().await {
            Ok(Some(bytes)) => {
                for event in parser.feed(&bytes) {
                    if let Some(data) = event.data {
                        match serde_json::from_str(&data) {
                            Ok(message) => {
                                let _ = signals.send(Signal::Message(message));
                            }
                            Err(e) => tracing::warn!("ignored a message that is not valid: {e}"),
                        }
                    }
                    if let Some(id) = event.id {
                        let _ = signals.send(Signal::Position(id));
                    }
                }
            }
            Ok(None) => return End::Closed("the service closed the stream".into()),
            Err(e) => return End::Closed(crate::root_cause(&e)),
        }
    }
}

/// The wait before the next connection after a failure: 1 second after the first failure, doubled
/// after each failure that follows, to 60 seconds at most.
#[derive(Debug, Default)]
pub struct Backoff {
    failures: u32,
}

impl Backoff {
    const FIRST: Duration = Duration::from_secs(1);
    const MAX: Duration = Duration::from_secs(60);

    /// Counts a failure, and gives the wait. `random` is from 0 to 1. The wait is a random time between
    /// half and all of the full wait, so that many clients do not connect at the same time.
    pub fn fail(&mut self, random: f64) -> Duration {
        let full = Self::FIRST
            .saturating_mul(2_u32.saturating_pow(self.failures))
            .min(Self::MAX);
        self.failures = self.failures.saturating_add(1);
        full.mul_f64(0.5 + 0.5 * random.clamp(0.0, 1.0))
    }

    /// A stream that sent a message resets the wait.
    pub fn reset(&mut self) {
        self.failures = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use wiremock::matchers::{header, method};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn message(json: &str) -> Message {
        serde_json::from_str(json).unwrap()
    }

    #[test]
    fn example_of_the_spec_is_valid_and_shows_as_an_event() {
        let json = r#"{"kind":"event","reference":"stockpile:5f0c","text":"Depot\nRifle +2 (7)","source":"custos"}"#;
        let schema: serde_json::Value =
            serde_json::from_str(include_str!("../../../../spec/schemas/message.schema.json")).unwrap();
        let value: serde_json::Value = serde_json::from_str(json).unwrap();
        for member in schema["required"].as_array().unwrap() {
            assert!(value.get(member.as_str().unwrap()).is_some(), "{member}");
        }

        assert_eq!(
            message(json).action("Optio"),
            Some(Action::Show(Notification {
                reference: Some(Reference::Service("stockpile:5f0c".into())),
                title: "Optio".into(),
                body: "Depot\nRifle +2 (7)".into(),
                is_error: false,
            }))
        );
    }

    #[test]
    fn error_is_an_error_and_an_unknown_kind_is_an_event() {
        let error = message(r#"{"kind":"error","text":"No bot."}"#).action("Optio");
        assert!(matches!(error, Some(Action::Show(n)) if n.is_error && n.reference.is_none()));
        let future = message(r#"{"kind":"answer","text":"Yes."}"#).action("Optio");
        assert!(matches!(future, Some(Action::Show(n)) if !n.is_error));
    }

    #[test]
    fn reference_without_text_removes_and_an_empty_message_does_nothing() {
        assert_eq!(
            message(r#"{"kind":"event","reference":"s:1"}"#).action("Optio"),
            Some(Action::Remove(Reference::Service("s:1".into())))
        );
        assert_eq!(message(r#"{"kind":"event"}"#).action("Optio"), None);
    }

    #[test]
    fn long_text_is_cut() {
        let json = format!(r#"{{"kind":"event","text":"{}"}}"#, "é".repeat(MAX_TEXT + 1));
        let Some(Action::Show(n)) = message(&json).action("Optio") else {
            panic!()
        };
        assert_eq!(n.body.chars().count(), MAX_TEXT);
    }

    #[test]
    fn wait_doubles_to_the_limit_with_a_random_part_and_resets() {
        let mut backoff = Backoff::default();
        let waits: Vec<_> = (0..8).map(|_| backoff.fail(1.0).as_secs()).collect();
        assert_eq!(waits, [1, 2, 4, 8, 16, 32, 60, 60]);
        assert_eq!(backoff.fail(0.0), Duration::from_secs(30));

        backoff.reset();
        assert_eq!(backoff.fail(0.0), Duration::from_millis(500));
    }

    async fn signals(template: ResponseTemplate, position: Option<&str>) -> Vec<Signal> {
        let server = MockServer::start().await;
        let mut mock = Mock::given(method("GET"))
            .and(header("authorization", "Bearer t"))
            .and(header("accept", "text/event-stream"));
        if let Some(position) = position {
            mock = mock.and(header("last-event-id", position));
        }
        mock.respond_with(template).expect(1).mount(&server).await;

        let (sender, mut signals) = mpsc::unbounded_channel();
        let endpoint = Url::parse(&server.uri()).unwrap();
        run(
            client(Duration::from_secs(1)),
            endpoint,
            "t".into(),
            position.map(str::to_owned),
            sender,
        )
        .await;
        let mut received = Vec::new();
        while let Ok(signal) = signals.try_recv() {
            received.push(signal);
        }
        received
    }

    fn event_stream(body: &str) -> ResponseTemplate {
        ResponseTemplate::new(200).set_body_raw(body.to_owned(), "text/event-stream")
    }

    #[tokio::test]
    async fn open_stream_gives_the_position_and_the_messages_in_order() {
        let body = "id: 1-1\n\n: keep-alive\n\nid: 1-2\ndata: {\"kind\":\"event\",\"text\":\"a\"}\n\n";

        let received = signals(event_stream(body), Some("1-0")).await;

        assert_eq!(
            received,
            [
                Signal::Opened,
                Signal::Position("1-1".into()),
                Signal::Message(message(r#"{"kind":"event","text":"a"}"#)),
                Signal::Position("1-2".into()),
                Signal::Ended(End::Closed("the service closed the stream".into())),
            ]
        );
    }

    #[tokio::test]
    async fn message_that_is_not_valid_is_ignored() {
        let received = signals(event_stream("id: 1\ndata: {\"text\":\"no kind\"}\n\n"), None).await;

        assert_eq!(received[1], Signal::Position("1".into()));
        assert_eq!(received.len(), 3);
    }

    #[tokio::test]
    async fn statuses_map_to_the_actions_of_the_spec() {
        let end = |received: Vec<Signal>| received.last().cloned();
        assert_eq!(
            end(signals(ResponseTemplate::new(401), None).await),
            Some(Signal::Ended(End::Unauthorized))
        );
        assert_eq!(
            end(signals(ResponseTemplate::new(403), None).await),
            Some(Signal::Ended(End::Forbidden))
        );
        assert_eq!(
            end(signals(ResponseTemplate::new(404), None).await),
            Some(Signal::Ended(End::NotFound))
        );
        assert_eq!(
            end(signals(ResponseTemplate::new(429).insert_header("Retry-After", "60"), None).await),
            Some(Signal::Ended(End::RateLimited(Duration::from_secs(60))))
        );
        assert!(matches!(
            end(signals(ResponseTemplate::new(503), None).await),
            Some(Signal::Ended(End::Failed(_)))
        ));
        assert!(matches!(
            end(signals(ResponseTemplate::new(200).set_body_string("{}"), None).await),
            Some(Signal::Ended(End::Failed(_)))
        ));
    }
}
