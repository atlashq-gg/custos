//! The query of spec/06-query.md: the input of the user, the requests to the service, and the open
//! query of this client, as the overlay shows it.

use std::time::{Duration, Instant, SystemTime};

use reqwest::StatusCode;
use serde::{Deserialize, Serialize};
use url::Url;

use crate::lines::{ERROR_PROMPT, INDENT, PROMPT};
use crate::upload::Problem;

/// The most characters of a question.
pub const MAX_TEXT: usize = 500;
/// The time with no key after which the prompt closes and gives the keyboard back to the game.
pub const INPUT_TIMEOUT: Duration = Duration::from_secs(60);
/// The time after a deadline before Custos shows that the service is not in contact. The clocks of
/// Custos and of the service can differ by a few seconds.
pub const DEADLINE_GRACE: Duration = Duration::from_secs(5);
/// The longest time of one request. The service answers in 1 second (spec/06-query.md).
const REQUEST_TIMEOUT: Duration = Duration::from_secs(15);

/// What the user typed at the prompt of the overlay, or the key of a decision.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Input {
    /// A question. `/new <text>` starts a new conversation.
    Ask { text: String, new_conversation: bool },
    /// `/stop`, or the key of an accept or a refusal.
    Act(Action),
}

/// The command that stops the open query.
pub const STOP: &str = "/stop";
/// The command that sends a question with no context from the earlier queries.
pub const NEW: &str = "/new";

/// Reads a typed line. An empty line is `None`. The error is a text for the user.
pub fn parse(line: &str) -> Result<Option<Input>, String> {
    let line = line.trim();
    let (text, new_conversation) = match line.split_once(char::is_whitespace) {
        _ if line == STOP => return Ok(Some(Input::Act(Action::Stop))),
        _ if line == NEW => return Err(format!("Write a question after {NEW}.")),
        Some((NEW, text)) => (text.trim(), true),
        _ if line.starts_with('/') => {
            return Err(format!("{line} is not a command. {HELP} shows the commands."));
        }
        _ => (line, false),
    };
    if text.is_empty() {
        return Ok(None);
    }
    if text.chars().count() > MAX_TEXT {
        return Err(format!("A question has at most {MAX_TEXT} characters."));
    }
    Ok(Some(Input::Ask {
        text: text.to_owned(),
        new_conversation,
    }))
}

/// The command that shows the commands.
pub const HELP: &str = "/help";
/// The line that shows while the user types a question.
pub const AWAITING: &str = "Awaiting input...";

/// The line that tells the user the key of a question while the overlay does not have the keyboard.
#[must_use]
pub fn key_hint(key: &str) -> String {
    format!("[{key}] for input. See {HELP}.")
}

/// The lines of [`HELP`].
#[must_use]
pub fn help() -> [String; 6] {
    [
        "Enter sends the question.".into(),
        format!("{NEW} <question> starts a new conversation."),
        format!("{STOP} stops the open question."),
        format!("{HELP} shows this help."),
        "Y or N on an empty line accepts or refuses a change.".into(),
        "Escape cancels.".into(),
    ]
}

/// A key that the user pushes while the overlay takes the keyboard.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Key {
    Char(char),
    Backspace,
    Enter,
    Escape,
}

/// What a key does to the typed line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    /// The user types on.
    Continue,
    /// The user cancelled, or sent an empty line. The game gets the keyboard back.
    Close,
    /// Send this input. The game gets the keyboard back.
    Send(Input),
}

/// The line that the user types at the prompt of the overlay.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Entry {
    pub text: String,
    /// The error of the last Enter, until the next change of the text.
    pub error: Option<String>,
    /// True after [`HELP`], until the next change of the text.
    pub help: bool,
    /// The time of the start of the prompt, or of the last key.
    last_key: Instant,
}

impl Entry {
    /// An empty line, at the start of the prompt.
    #[must_use]
    pub fn new(now: Instant) -> Self {
        Self {
            text: String::new(),
            error: None,
            help: false,
            last_key: now,
        }
    }

    /// True when the user pushed no key for [`INPUT_TIMEOUT`].
    #[must_use]
    pub fn is_idle(&self, now: Instant) -> bool {
        now >= self.last_key + INPUT_TIMEOUT
    }

    /// Applies a key. `waiting` is true while a question of the service waits for a decision: then
    /// `Y` and `N` on an empty line decide it.
    pub fn key(&mut self, key: Key, waiting: bool, now: Instant) -> Outcome {
        self.last_key = now;
        match key {
            Key::Escape => return Outcome::Close,
            Key::Enter if self.text.trim() == HELP => {
                self.text.clear();
                self.error = None;
                self.help = true;
                return Outcome::Continue;
            }
            Key::Enter => {
                return match parse(&self.text) {
                    Ok(Some(input)) => Outcome::Send(input),
                    Ok(None) => Outcome::Close,
                    Err(e) => {
                        self.error = Some(e);
                        Outcome::Continue
                    }
                };
            }
            Key::Char(c) if waiting && self.text.is_empty() && c.eq_ignore_ascii_case(&'y') => {
                return Outcome::Send(Input::Act(Action::Accept));
            }
            Key::Char(c) if waiting && self.text.is_empty() && c.eq_ignore_ascii_case(&'n') => {
                return Outcome::Send(Input::Act(Action::Refuse));
            }
            Key::Char(c) if !c.is_control() && self.text.chars().count() < MAX_TEXT => self.text.push(c),
            Key::Char(_) => return Outcome::Continue,
            Key::Backspace => {
                self.text.pop();
            }
        }
        self.error = None;
        self.help = false;
        Outcome::Continue
    }
}

/// An action for the open query.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Action {
    Accept,
    Refuse,
    Stop,
}

impl Action {
    /// True when the service permits the action in the state.
    #[must_use]
    pub fn permitted(self, state: State) -> bool {
        match self {
            Action::Accept | Action::Refuse => state == State::Waiting,
            Action::Stop => matches!(state, State::Working | State::Waiting),
        }
    }
}

/// A new reference: 32 random hexadecimal digits.
#[must_use]
pub fn new_reference() -> String {
    hex::encode(rand::random::<[u8; 16]>())
}

#[derive(Serialize)]
#[serde(untagged)]
enum Body<'a> {
    Query {
        reference: &'a str,
        text: &'a str,
        #[serde(skip_serializing_if = "std::ops::Not::not")]
        new_conversation: bool,
    },
    Action {
        reference: &'a str,
        action: Action,
    },
}

/// The state document of a response.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct StateDocument {
    pub reference: String,
    pub state: String,
    pub deadline: Option<String>,
    pub cause: Option<String>,
}

/// What the service answered, with the action of Custos in the table of spec/06-query.md.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Response {
    /// `200` or `202`.
    Accepted(StateDocument),
    /// `401`: the challenge steps.
    Unauthorized,
    /// `404`: for a query, get the discovery document again. For an action, remove the query.
    NotFound,
    /// `400`, `403`, `409`, or `429`: report the error. A `429` has the wait.
    Refused {
        status: u16,
        problem: Problem,
        retry_after: Option<Duration>,
    },
    /// `5xx` or an unexpected status: report the error.
    Failed { status: u16, problem: Problem },
}

impl Response {
    /// The text of the error for the user.
    #[must_use]
    pub fn error_text(&self, service: &str) -> String {
        match self {
            Response::Accepted(_) => String::new(),
            Response::Unauthorized => "Sign in to ask a question.".into(),
            Response::NotFound => format!("{service} has no query now."),
            Response::Refused {
                status,
                problem,
                retry_after,
            } => {
                let text = problem.text(&format!("{service} refused the question ({status})."));
                match retry_after {
                    Some(wait) => format!("{text} Try again in {} s.", wait.as_secs()),
                    None => text,
                }
            }
            Response::Failed { status, problem } => problem.text(&format!("{service} failed ({status}). Try again.")),
        }
    }
}

/// Sends a question. Only a transport error is an `Err`.
pub async fn ask(
    http: &reqwest::Client,
    endpoint: &Url,
    token: &str,
    reference: &str,
    text: &str,
    new_conversation: bool,
) -> Result<Response, reqwest::Error> {
    let body = Body::Query {
        reference,
        text,
        new_conversation,
    };
    send(http, endpoint, token, &body).await
}

/// Sends an action for the query with the reference. Only a transport error is an `Err`.
pub async fn act(
    http: &reqwest::Client,
    endpoint: &Url,
    token: &str,
    reference: &str,
    action: Action,
) -> Result<Response, reqwest::Error> {
    send(http, endpoint, token, &Body::Action { reference, action }).await
}

async fn send(
    http: &reqwest::Client,
    endpoint: &Url,
    token: &str,
    body: &Body<'_>,
) -> Result<Response, reqwest::Error> {
    let response = http
        .post(endpoint.clone())
        .timeout(REQUEST_TIMEOUT)
        .bearer_auth(token)
        .json(body)
        .send()
        .await?;
    let status = response.status();
    Ok(match status {
        StatusCode::OK | StatusCode::ACCEPTED => Response::Accepted(response.json().await?),
        StatusCode::UNAUTHORIZED => Response::Unauthorized,
        StatusCode::NOT_FOUND => Response::NotFound,
        StatusCode::BAD_REQUEST | StatusCode::FORBIDDEN | StatusCode::CONFLICT | StatusCode::TOO_MANY_REQUESTS => {
            let retry_after =
                (status == StatusCode::TOO_MANY_REQUESTS).then(|| crate::upload::retry_after(response.headers()));
            Response::Refused {
                status: status.as_u16(),
                problem: response.json().await.unwrap_or_default(),
                retry_after,
            }
        }
        _ => Response::Failed {
            status: status.as_u16(),
            problem: response.json().await.unwrap_or_default(),
        },
    })
}

/// The state of a query. [`State::Sending`] is the state of Custos before the response.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum State {
    Sending,
    Working,
    Waiting,
    Applying,
    Closed,
}

impl State {
    /// The state of the service with this name, or `None` for a name that Custos does not know.
    #[must_use]
    pub fn parse(name: &str) -> Option<Self> {
        match name {
            "working" => Some(State::Working),
            "waiting" => Some(State::Waiting),
            "applying" => Some(State::Applying),
            "closed" => Some(State::Closed),
            _ => None,
        }
    }
}

/// The cause of the end of a query. A cause that Custos does not know is [`Cause::Failed`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Cause {
    Answer,
    Refused,
    Expired,
    Replaced,
    Stopped,
    Failed,
    Interrupted,
    ResultUnknown,
}

impl Cause {
    #[must_use]
    pub fn parse(name: &str) -> Self {
        match name {
            "answer" => Cause::Answer,
            "refused" => Cause::Refused,
            "expired" => Cause::Expired,
            "replaced" => Cause::Replaced,
            "stopped" => Cause::Stopped,
            "interrupted" => Cause::Interrupted,
            "result_unknown" => Cause::ResultUnknown,
            _ => Cause::Failed,
        }
    }

    /// True for an end that the user must know as an error.
    #[must_use]
    pub fn is_error(self) -> bool {
        matches!(self, Cause::Failed | Cause::Interrupted | Cause::ResultUnknown)
    }

    /// The text of the end when the service gives none. `service` is the name of the service.
    fn text(self, service: &str) -> String {
        match self {
            Cause::Answer => "No answer.".into(),
            Cause::Refused => "You refused the change.".into(),
            Cause::Expired => "The question expired.".into(),
            Cause::Replaced => "A new question replaced this question.".into(),
            Cause::Stopped => "You stopped the question.".into(),
            Cause::Failed => "The question failed.".into(),
            Cause::Interrupted => format!("{service} stopped while it worked on the question."),
            Cause::ResultUnknown => "The result of the change is not known.".into(),
        }
    }
}

/// A message of a query from the stream, or a state document of a response.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Update {
    pub reference: String,
    pub state: State,
    pub text: Option<String>,
    pub deadline: Option<SystemTime>,
    pub cause: Option<Cause>,
}

impl Update {
    /// The update of a message or a state document. `None` for a state that Custos does not know.
    #[must_use]
    pub fn new(
        reference: String,
        state: &str,
        text: Option<String>,
        deadline: Option<&str>,
        cause: Option<&str>,
    ) -> Option<Self> {
        Some(Self {
            reference,
            state: State::parse(state)?,
            text: text.map(|text| text.chars().take(crate::stream::MAX_QUERY_TEXT).collect()),
            deadline: deadline.and_then(parse_time),
            cause: cause.map(Cause::parse),
        })
    }
}

impl From<StateDocument> for Option<Update> {
    fn from(document: StateDocument) -> Self {
        Update::new(
            document.reference,
            &document.state,
            None,
            document.deadline.as_deref(),
            document.cause.as_deref(),
        )
    }
}

fn parse_time(text: &str) -> Option<SystemTime> {
    time::OffsetDateTime::parse(text, &time::format_description::well_known::Rfc3339)
        .ok()
        .map(SystemTime::from)
}

/// The open query of this client, or its last query.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Query {
    pub reference: String,
    /// The name of the service, from its discovery document.
    pub service: String,
    /// The question of the user. `None` for a query that Custos learned from the stream only, for
    /// example after a restart.
    pub question: Option<String>,
    pub state: State,
    /// The text of the state: the step, the question to the user, or the answer.
    pub text: Option<String>,
    /// The time of the next message of the service. In the state `waiting`, also the end of the
    /// question.
    pub deadline: Option<SystemTime>,
    pub cause: Option<Cause>,
    /// True for an end that Custos made, for example after a refusal of the request.
    pub local_error: bool,
    /// The time when the query closed, for the lifetime of the answer in the overlay.
    pub closed_at: Option<Instant>,
}

/// A line of the query, as in the overlay.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Line {
    pub prompt: &'static str,
    pub text: String,
    pub tone: Tone,
}

/// The color of a line.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tone {
    Normal,
    Error,
    /// The line that tells the user that the overlay has the keyboard.
    Input,
}

impl Line {
    /// A line with the indent of a next line of a message.
    #[must_use]
    pub fn indented(text: impl Into<String>) -> Self {
        Self {
            prompt: INDENT,
            text: text.into(),
            tone: Tone::Normal,
        }
    }
}

impl Query {
    /// A question that Custos sends now to the service with this name.
    #[must_use]
    pub fn sending(reference: String, service: String, question: String) -> Self {
        Self {
            reference,
            service,
            question: Some(question),
            state: State::Sending,
            text: None,
            deadline: None,
            cause: None,
            local_error: false,
            closed_at: None,
        }
    }

    /// A query that Custos learned from the stream of the service with this name.
    #[must_use]
    pub fn learned(update: Update, service: String, now: Instant) -> Self {
        let mut query = Self::sending(update.reference.clone(), service, String::new());
        query.question = None;
        query.apply(update, now);
        query
    }

    /// True until the query closed.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.state != State::Closed
    }

    /// Applies a message of the stream. A closed query does not change.
    pub fn apply(&mut self, update: Update, now: Instant) {
        if !self.is_open() {
            return;
        }
        self.state = update.state;
        self.text = update.text;
        self.deadline = update.deadline;
        self.cause = update.cause;
        if update.state == State::Closed {
            self.deadline = None;
            self.cause.get_or_insert(Cause::Failed);
            self.closed_at = Some(now);
        }
    }

    /// Closes the query with an error of Custos.
    pub fn fail(&mut self, text: String, now: Instant) {
        self.state = State::Closed;
        self.text = Some(text);
        self.deadline = None;
        self.cause = Some(Cause::Failed);
        self.local_error = true;
        self.closed_at = Some(now);
    }

    /// True when the deadline of an open query went by with no new message.
    #[must_use]
    pub fn no_contact(&self, now: SystemTime) -> bool {
        self.is_open() && self.deadline.is_some_and(|deadline| now > deadline + DEADLINE_GRACE)
    }

    /// True while the overlay shows the query: while it is open, and for `lifetime` after its end.
    #[must_use]
    pub fn shows(&self, now: Instant, lifetime: Duration) -> bool {
        self.closed_at.is_none_or(|closed| now < closed + lifetime)
    }

    /// The lines of the query at `now`. `key` is the name of the key that starts a question. With
    /// `max_lines`, the text of the end has at most that number of lines, and its last line tells the
    /// user that the key shows the full text.
    #[must_use]
    pub fn lines(&self, now: SystemTime, key: &str, max_lines: Option<usize>) -> Vec<Line> {
        let mut lines = Vec::new();
        let mut add = |text: &str, is_error: bool| {
            for (i, text) in text.split('\n').enumerate() {
                let prompt = if i == 0 && is_error { ERROR_PROMPT } else { INDENT };
                lines.push(Line {
                    prompt,
                    text: text.chars().map(|c| if c.is_control() { ' ' } else { c }).collect(),
                    tone: if is_error { Tone::Error } else { Tone::Normal },
                });
            }
        };
        let step = |label: &str| match &self.text {
            Some(text) => format!("{label}: {text}"),
            None => label.to_owned(),
        };
        match self.state {
            State::Sending => add("Sent", false),
            State::Working => add(&step("Working"), false),
            State::Applying => add(&step("Applying the change"), false),
            State::Waiting => {
                let seconds = self
                    .deadline
                    .and_then(|deadline| deadline.duration_since(now).ok())
                    .map_or(0, |left| left.as_secs());
                let question = self.text.as_deref().unwrap_or("Accept the change?");
                add(&format!("{question} [Y/N] ({seconds} s)"), false);
            }
            State::Closed => {
                let cause = self.cause.unwrap_or(Cause::Failed);
                let fallback = cause.text(&self.service);
                let text = self.text.as_deref().unwrap_or(&fallback);
                let is_error = self.local_error || cause.is_error();
                let mut text_lines: Vec<&str> = text.split('\n').collect();
                if let Some(max) = max_lines.filter(|&max| text_lines.len() > max) {
                    text_lines.truncate(max.saturating_sub(1));
                    let more = format!("{key} shows the full text");
                    add(&text_lines.join("\n"), is_error);
                    add(&more, false);
                } else {
                    add(text, is_error);
                }
            }
        }
        if self.no_contact(now) {
            add(&format!("No contact with {}", self.service), true);
        }
        if let Some(question) = &self.question {
            lines.insert(
                0,
                Line {
                    prompt: PROMPT,
                    text: question.clone(),
                    tone: Tone::Normal,
                },
            );
        }
        lines
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use wiremock::matchers::{body_json, header, method};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    #[test]
    fn input_reads_the_question_and_the_commands() {
        let ask = |text: &str, new_conversation| {
            Ok(Some(Input::Ask {
                text: text.into(),
                new_conversation,
            }))
        };
        assert_eq!(parse("  How many rifles? "), ask("How many rifles?", false));
        assert_eq!(parse("/new How many rifles?"), ask("How many rifles?", true));
        assert_eq!(parse("/stop"), Ok(Some(Input::Act(Action::Stop))));
        assert_eq!(parse("   "), Ok(None));
        assert!(parse("/new").is_err());
        assert!(parse("/help").is_err());
        assert!(parse(&"a".repeat(MAX_TEXT + 1)).is_err());
        assert_eq!(parse(&"a".repeat(MAX_TEXT)).map(|i| i.is_some()), Ok(true));
    }

    #[test]
    fn entry_types_sends_and_decides() {
        let now = Instant::now();
        let mut entry = Entry::new(now);
        for c in "Hi!".chars() {
            assert_eq!(entry.key(Key::Char(c), false, now), Outcome::Continue);
        }
        entry.key(Key::Backspace, false, now);
        assert_eq!(entry.text, "Hi");
        assert_eq!(
            entry.key(Key::Enter, false, now),
            Outcome::Send(Input::Ask {
                text: "Hi".into(),
                new_conversation: false
            })
        );

        let mut entry = Entry::new(now);
        assert_eq!(
            entry.key(Key::Char('Y'), true, now),
            Outcome::Send(Input::Act(Action::Accept))
        );
        assert_eq!(
            entry.key(Key::Char('n'), true, now),
            Outcome::Send(Input::Act(Action::Refuse))
        );
        assert_eq!(entry.key(Key::Char('n'), false, now), Outcome::Continue);
        assert_eq!(entry.key(Key::Char('y'), true, now), Outcome::Continue);
        assert_eq!(entry.text, "ny");
        assert_eq!(Entry::new(now).key(Key::Enter, false, now), Outcome::Close);
        assert_eq!(entry.key(Key::Escape, false, now), Outcome::Close);
    }

    #[test]
    fn prompt_is_idle_after_the_timeout_and_each_key_starts_it_again() {
        let start = Instant::now();
        let mut entry = Entry::new(start);
        assert!(!entry.is_idle(start + Duration::from_secs(59)));
        assert!(entry.is_idle(start + INPUT_TIMEOUT));

        let typed = start + Duration::from_secs(50);
        entry.key(Key::Char('a'), false, typed);
        assert!(!entry.is_idle(start + INPUT_TIMEOUT));
        assert!(entry.is_idle(typed + INPUT_TIMEOUT));
    }

    #[test]
    fn key_hint_names_the_key_and_the_help() {
        assert_eq!(key_hint("Ctrl+Alt+K"), "[Ctrl+Alt+K] for input. See /help.");
    }

    #[test]
    fn help_shows_until_the_next_change() {
        let now = Instant::now();
        let mut entry = Entry::new(now);
        for c in " /help".chars() {
            entry.key(Key::Char(c), false, now);
        }
        assert_eq!(entry.key(Key::Enter, false, now), Outcome::Continue);
        assert!(entry.help);
        assert_eq!(entry.text, "");
        entry.key(Key::Char('a'), false, now);
        assert!(!entry.help);
        assert!(help().iter().any(|line| line.contains(HELP)));
    }

    #[test]
    fn entry_shows_an_error_until_the_next_change_and_has_a_limit() {
        let now = Instant::now();
        let mut entry = Entry::new(now);
        for c in "/hepl".chars() {
            entry.key(Key::Char(c), false, now);
        }
        assert_eq!(entry.key(Key::Enter, false, now), Outcome::Continue);
        assert!(entry.error.is_some());
        entry.key(Key::Backspace, false, now);
        assert_eq!(entry.error, None);

        let mut entry = Entry {
            text: "a".repeat(MAX_TEXT),
            ..Entry::new(now)
        };
        entry.key(Key::Char('b'), false, now);
        assert_eq!(entry.text.len(), MAX_TEXT);
    }

    #[test]
    fn reference_obeys_the_rule_of_the_spec() {
        let reference = new_reference();
        assert_eq!(reference.len(), 32);
        assert!(reference.chars().all(|c| c.is_ascii_alphanumeric()));
        assert_ne!(reference, new_reference());
    }

    #[test]
    fn bodies_match_the_schemas_of_the_spec() {
        let query = serde_json::to_value(Body::Query {
            reference: "r1234567",
            text: "Hi",
            new_conversation: false,
        })
        .unwrap();
        assert_eq!(query, serde_json::json!({"reference": "r1234567", "text": "Hi"}));
        let new = serde_json::to_value(Body::Query {
            reference: "r1234567",
            text: "Hi",
            new_conversation: true,
        })
        .unwrap();
        assert_eq!(new["new_conversation"], true);
        let action = serde_json::to_value(Body::Action {
            reference: "r1234567",
            action: Action::Accept,
        })
        .unwrap();
        assert_eq!(action, serde_json::json!({"reference": "r1234567", "action": "accept"}));

        let schema: serde_json::Value =
            serde_json::from_str(include_str!("../../../../spec/schemas/query.schema.json")).unwrap();
        for member in schema["required"].as_array().unwrap() {
            assert!(query.get(member.as_str().unwrap()).is_some(), "{member}");
        }
    }

    #[test]
    fn actions_are_permitted_only_in_their_states() {
        assert!(Action::Accept.permitted(State::Waiting));
        assert!(!Action::Refuse.permitted(State::Working));
        assert!(Action::Stop.permitted(State::Working));
        assert!(!Action::Stop.permitted(State::Applying));
    }

    #[tokio::test]
    async fn responses_map_to_the_actions_of_the_spec() {
        let server = MockServer::start().await;
        let endpoint: Url = format!("{}/api/v1/query", server.uri()).parse().unwrap();
        Mock::given(method("POST"))
            .and(header("authorization", "Bearer t"))
            .and(body_json(serde_json::json!({"reference": "accepted", "text": "Hi"})))
            .respond_with(ResponseTemplate::new(202).set_body_json(serde_json::json!({
                "reference": "accepted", "state": "working", "deadline": "2026-11-25T20:00:05Z"
            })))
            .mount(&server)
            .await;
        Mock::given(method("POST"))
            .and(body_json(serde_json::json!({"reference": "limited", "text": "Hi"})))
            .respond_with(
                ResponseTemplate::new(429)
                    .insert_header("retry-after", "30")
                    .set_body_json(serde_json::json!({"title": "Too many queries"})),
            )
            .mount(&server)
            .await;
        Mock::given(method("POST"))
            .and(body_json(
                serde_json::json!({"reference": "accepted", "action": "stop"}),
            ))
            .respond_with(ResponseTemplate::new(409))
            .mount(&server)
            .await;
        let http = reqwest::Client::new();

        let Ok(Response::Accepted(document)) = ask(&http, &endpoint, "t", "accepted", "Hi", false).await else {
            panic!()
        };
        let update = Option::<Update>::from(document).unwrap();
        assert_eq!(update.state, State::Working);
        assert_eq!(update.deadline, parse_time("2026-11-25T20:00:05Z"));
        assert!(update.deadline.is_some());

        let limited = ask(&http, &endpoint, "t", "limited", "Hi", false).await.unwrap();
        assert_eq!(limited.error_text("Optio"), "Too many queries Try again in 30 s.");

        let conflict = act(&http, &endpoint, "t", "accepted", Action::Stop).await.unwrap();
        assert!(matches!(conflict, Response::Refused { status: 409, .. }));
    }

    fn update(state: &str, text: Option<&str>, cause: Option<&str>) -> Update {
        Update::new(
            "r".into(),
            state,
            text.map(Into::into),
            Some("2026-11-25T20:00:05Z"),
            cause,
        )
        .unwrap()
    }

    fn texts(lines: &[Line]) -> Vec<String> {
        lines.iter().map(|l| format!("{}{}", l.prompt, l.text)).collect()
    }

    #[test]
    fn lines_show_the_question_and_each_state() {
        let now = Instant::now();
        let at = parse_time("2026-11-25T20:00:00Z").unwrap();
        let mut query = Query::sending("r".into(), "Optio".into(), "How many rifles?".into());
        assert_eq!(
            texts(&query.lines(at, "Ctrl+Alt+K", None)),
            ["> How many rifles?", "  Sent"]
        );

        query.apply(update("working", Some("Custos: Find items"), None), now);
        assert_eq!(
            texts(&query.lines(at, "Ctrl+Alt+K", None)),
            ["> How many rifles?", "  Working: Custos: Find items"]
        );

        query.apply(update("waiting", Some("Set Label? \"Main Depot\""), None), now);
        assert_eq!(
            texts(&query.lines(at, "Ctrl+Alt+K", None)),
            ["> How many rifles?", "  Set Label? \"Main Depot\" [Y/N] (5 s)"]
        );

        query.apply(update("closed", Some("Line 1\nLine 2\nLine 3"), Some("answer")), now);
        assert_eq!(query.closed_at, Some(now));
        assert_eq!(
            texts(&query.lines(at, "Ctrl+Alt+K", Some(2))),
            ["> How many rifles?", "  Line 1", "  Ctrl+Alt+K shows the full text"]
        );
        assert_eq!(query.lines(at, "K", None).len(), 4);

        // A closed query does not change.
        query.apply(update("working", None, None), now);
        assert_eq!(query.state, State::Closed);
    }

    #[test]
    fn an_end_with_an_error_and_an_unknown_cause_are_errors() {
        let now = Instant::now();
        let at = SystemTime::UNIX_EPOCH;
        let mut query = Query::learned(update("closed", None, Some("power_failure")), "Optio".into(), now);
        assert_eq!(query.question, None);
        assert_eq!(texts(&query.lines(at, "K", None)), ["! The question failed."]);
        assert_eq!(query.lines(at, "K", None)[0].tone, Tone::Error);

        query = Query::sending("r".into(), "Optio".into(), "Hi".into());
        query.fail("Optio refused the question.".into(), now);
        assert_eq!(
            texts(&query.lines(at, "K", None)),
            ["> Hi", "! Optio refused the question."]
        );
    }

    #[test]
    fn no_contact_after_the_deadline_and_the_answer_goes_after_its_lifetime() {
        let now = Instant::now();
        let deadline = parse_time("2026-11-25T20:00:05Z").unwrap();
        let mut query = Query::learned(update("working", None, None), "Optio".into(), now);
        assert!(!query.no_contact(deadline + DEADLINE_GRACE));
        assert!(query.no_contact(deadline + DEADLINE_GRACE + Duration::from_secs(1)));
        let lines = query.lines(deadline + Duration::from_secs(60), "K", None);
        assert_eq!(texts(&lines).last().unwrap(), "! No contact with Optio");

        let lifetime = Duration::from_secs(180);
        assert!(query.shows(now + Duration::from_secs(1000), lifetime));
        query.apply(update("closed", Some("Done."), Some("answer")), now);
        assert!(!query.no_contact(deadline + Duration::from_secs(60)));
        assert!(query.shows(now + Duration::from_secs(179), lifetime));
        assert!(!query.shows(now + lifetime, lifetime));
    }
}
