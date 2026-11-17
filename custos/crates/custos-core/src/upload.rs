//! The upload of spec/03-upload.md.

use std::time::Duration;

use reqwest::StatusCode;
use serde::Deserialize;
use url::Url;

/// The result document. Unstable in contract version v0: unknown members are ignored.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct UploadResult {
    pub status: String,
    pub received_at: String,
    pub size_bytes: u64,
    pub sha256: String,
    pub war: Option<War>,
    #[serde(default)]
    pub stockpiles: Vec<Stockpile>,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct War {
    pub war_id: String,
    pub war_number: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct Stockpile {
    pub name: String,
    pub location: Option<String>,
    pub item_count: Option<u64>,
}

/// The `title` and `detail` of an RFC 9457 problem document.
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
pub struct Problem {
    pub title: Option<String>,
    pub detail: Option<String>,
}

impl Problem {
    /// The detail, else the title, else `fallback`.
    #[must_use]
    pub fn text(&self, fallback: &str) -> String {
        self.detail
            .clone()
            .or_else(|| self.title.clone())
            .unwrap_or_else(|| fallback.to_owned())
    }
}

/// What the service answered, with the action of Custos in the table of spec/03-upload.md.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    /// `200`.
    Accepted(UploadResult),
    /// `401`: refresh the token or log in.
    Unauthorized,
    /// `404`: get the discovery document again.
    NotFound,
    /// `413`, `415`, or `422`: do not send the same file again.
    Rejected { status: u16, problem: Problem },
    /// `429`: wait.
    RateLimited { retry_after: Duration },
    /// `5xx` or an unexpected status: try again at the next interval.
    Failed { status: u16, problem: Problem },
}

/// The wait when a `429` has no usable `Retry-After` header.
const DEFAULT_RETRY_AFTER: Duration = Duration::from_secs(60);

/// Sends the full file. Only a transport error is an `Err`.
pub async fn send(
    http: &reqwest::Client,
    endpoint: &Url,
    access_token: &str,
    file: Vec<u8>,
) -> Result<Outcome, reqwest::Error> {
    let response = http
        .post(endpoint.clone())
        .bearer_auth(access_token)
        .header(reqwest::header::CONTENT_TYPE, "application/octet-stream")
        .body(file)
        .send()
        .await?;

    let status = response.status();
    Ok(match status {
        StatusCode::OK => Outcome::Accepted(response.json().await?),
        StatusCode::UNAUTHORIZED => Outcome::Unauthorized,
        StatusCode::NOT_FOUND => Outcome::NotFound,
        StatusCode::TOO_MANY_REQUESTS => Outcome::RateLimited {
            retry_after: retry_after(&response),
        },
        StatusCode::PAYLOAD_TOO_LARGE | StatusCode::UNSUPPORTED_MEDIA_TYPE | StatusCode::UNPROCESSABLE_ENTITY => {
            Outcome::Rejected {
                status: status.as_u16(),
                problem: problem(response).await,
            }
        }
        _ => Outcome::Failed {
            status: status.as_u16(),
            problem: problem(response).await,
        },
    })
}

/// `Retry-After` in seconds. The HTTP-date form is not supported and gives the default.
fn retry_after(response: &reqwest::Response) -> Duration {
    response
        .headers()
        .get(reqwest::header::RETRY_AFTER)
        .and_then(|value| value.to_str().ok()?.trim().parse().ok())
        .map_or(DEFAULT_RETRY_AFTER, Duration::from_secs)
}

async fn problem(response: reqwest::Response) -> Problem {
    response.json().await.unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use wiremock::matchers::{header, method};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    const RESULT: &str = r#"{
        "status": "accepted", "received_at": "2026-11-17T14:05:00Z", "size_bytes": 4,
        "sha256": "a3f5c1d2e4b6a8c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4",
        "war": { "war_id": "w", "war_number": 142 }, "stockpiles": [], "future": 1
    }"#;

    async fn answer(template: ResponseTemplate) -> Outcome {
        let server = MockServer::start().await;
        Mock::given(method("POST"))
            .and(header("content-type", "application/octet-stream"))
            .and(header("authorization", "Bearer t"))
            .respond_with(template)
            .mount(&server)
            .await;
        let endpoint = Url::parse(&server.uri()).unwrap();
        send(&reqwest::Client::new(), &endpoint, "t", b"GVAS".to_vec())
            .await
            .unwrap()
    }

    #[tokio::test]
    async fn accepted_result_is_read() {
        let Outcome::Accepted(result) =
            answer(ResponseTemplate::new(200).set_body_raw(RESULT, "application/json")).await
        else {
            panic!()
        };
        assert_eq!(result.war.unwrap().war_number, 142);
    }

    #[tokio::test]
    async fn rate_limit_reads_retry_after() {
        let outcome = answer(ResponseTemplate::new(429).insert_header("Retry-After", "17")).await;
        assert_eq!(
            outcome,
            Outcome::RateLimited {
                retry_after: Duration::from_secs(17)
            }
        );
    }

    #[tokio::test]
    async fn rejection_keeps_the_problem_detail() {
        let outcome = answer(ResponseTemplate::new(422).set_body_raw(
            r#"{"title":"Not a save file","detail":"Not GVAS."}"#,
            "application/problem+json",
        ))
        .await;
        let Outcome::Rejected { status: 422, problem } = outcome else {
            panic!()
        };
        assert_eq!(problem.text("x"), "Not GVAS.");
    }

    #[tokio::test]
    async fn statuses_map_to_outcomes() {
        assert_eq!(answer(ResponseTemplate::new(401)).await, Outcome::Unauthorized);
        assert_eq!(answer(ResponseTemplate::new(404)).await, Outcome::NotFound);
        assert!(matches!(
            answer(ResponseTemplate::new(503)).await,
            Outcome::Failed { status: 503, .. }
        ));
    }
}
