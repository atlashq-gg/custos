//! The login of spec/02-authentication.md: Authorization Code with PKCE and the redirect URI
//! [`REDIRECT_URI`], which the operating system gives to Custos as a deep link.

use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use url::Url;

use crate::discovery::Service;

/// The redirect URI of Custos. The application registers the scheme `custos` with the desktop.
pub const REDIRECT_URI: &str = "custos://oauth2callback";

/// Refresh an access token this many seconds before it expires.
const EXPIRY_MARGIN_SECONDS: u64 = 30;

#[derive(Debug, thiserror::Error)]
pub enum AuthError {
    #[error("the login link is not for Custos: {0}")]
    NotACallback(String),
    #[error("the login link belongs to a different login. Start the login again.")]
    StateMismatch,
    #[error("the service refused the login: {0}")]
    Denied(String),
    #[error("the login link has no code")]
    NoCode,
    #[error("the token endpoint refused the request: {detail}")]
    TokenRefused { status: u16, detail: String },
    #[error("cannot reach the token endpoint: {0}")]
    Http(#[from] reqwest::Error),
}

impl AuthError {
    /// True when the token endpoint refused the grant, for example an expired refresh token
    /// (`invalid_grant`). Then only a new login helps. A network error or an error of the service is
    /// not a refusal: the same request can work later.
    #[must_use]
    pub fn is_refusal(&self) -> bool {
        matches!(self, AuthError::TokenRefused { status: 400 | 401, .. })
    }
}

/// The tokens of one service. Custos does not read the content of a token.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Tokens {
    pub access_token: String,
    pub refresh_token: Option<String>,
    /// Seconds since the Unix epoch.
    pub expires_at: Option<u64>,
}

impl Tokens {
    /// True when the access token expires in less than the margin.
    #[must_use]
    pub fn is_expiring(&self, now: u64) -> bool {
        self.expires_at.is_some_and(|at| expires_soon(at, now))
    }
}

/// True when a token that expires at `expires_at` expires in less than the margin. Both are seconds
/// since the Unix epoch.
#[must_use]
pub fn expires_soon(expires_at: u64, now: u64) -> bool {
    expires_at <= now + EXPIRY_MARGIN_SECONDS
}

/// A login that waits for the browser to open the redirect URI.
#[derive(Debug, Clone)]
pub struct PendingLogin {
    state: String,
    verifier: String,
    pub authorize_url: Url,
}

/// Starts a login. Open [`PendingLogin::authorize_url`] in the browser.
#[must_use]
pub fn begin(service: &Service) -> PendingLogin {
    let verifier = random_token();
    let state = random_token();
    let challenge = URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()));

    let mut authorize_url = service.authorization_server.authorization_endpoint.clone();
    authorize_url
        .query_pairs_mut()
        .append_pair("response_type", "code")
        .append_pair("client_id", &service.discovery.client_id)
        .append_pair("redirect_uri", REDIRECT_URI)
        .append_pair("scope", &service.discovery.scopes.join(" "))
        .append_pair("state", &state)
        .append_pair("code_challenge", &challenge)
        .append_pair("code_challenge_method", "S256");

    PendingLogin {
        state,
        verifier,
        authorize_url,
    }
}

/// True when `link` is the redirect URI of Custos, with or without a `/` before the query.
#[must_use]
pub fn is_callback(link: &str) -> bool {
    Url::parse(link).is_ok_and(|url| {
        url.scheme() == "custos" && url.host_str() == Some("oauth2callback") && matches!(url.path(), "" | "/")
    })
}

/// Reads the authorization code from the redirect URI that the browser opened.
pub fn code_from_callback(pending: &PendingLogin, link: &str) -> Result<String, AuthError> {
    if !is_callback(link) {
        return Err(AuthError::NotACallback(link.to_owned()));
    }
    let url = Url::parse(link).map_err(|_| AuthError::NotACallback(link.to_owned()))?;
    let param = |name: &str| url.query_pairs().find(|(k, _)| k == name).map(|(_, v)| v.into_owned());

    if param("state").as_deref() != Some(pending.state.as_str()) {
        return Err(AuthError::StateMismatch);
    }
    if let Some(error) = param("error") {
        let description = param("error_description").unwrap_or_default();
        return Err(AuthError::Denied(format!("{error} {description}").trim().to_owned()));
    }
    param("code").ok_or(AuthError::NoCode)
}

/// Exchanges the authorization code for tokens.
pub async fn exchange_code(
    http: &reqwest::Client,
    service: &Service,
    pending: &PendingLogin,
    code: &str,
) -> Result<Tokens, AuthError> {
    request_tokens(
        http,
        service,
        &[
            ("grant_type", "authorization_code"),
            ("client_id", &service.discovery.client_id),
            ("redirect_uri", REDIRECT_URI),
            ("code", code),
            ("code_verifier", &pending.verifier),
        ],
        None,
    )
    .await
}

/// Gets a new access token with the refresh token. A response without a new refresh token keeps the old one.
pub async fn refresh(http: &reqwest::Client, service: &Service, refresh_token: &str) -> Result<Tokens, AuthError> {
    request_tokens(
        http,
        service,
        &[
            ("grant_type", "refresh_token"),
            ("client_id", &service.discovery.client_id),
            ("refresh_token", refresh_token),
        ],
        Some(refresh_token),
    )
    .await
}

/// Revokes the refresh token when the service has a revocation endpoint. A failure is not an error: the
/// tokens are deleted locally in any case.
pub async fn revoke(http: &reqwest::Client, service: &Service, tokens: &Tokens) {
    let (Some(endpoint), Some(token)) = (&service.authorization_server.revocation_endpoint, &tokens.refresh_token)
    else {
        return;
    };
    let form = [
        ("client_id", service.discovery.client_id.as_str()),
        ("token", token),
        ("token_type_hint", "refresh_token"),
    ];
    if let Err(e) = http.post(endpoint.clone()).form(&form).send().await {
        tracing::warn!("cannot revoke the refresh token: {e}");
    }
}

#[derive(Deserialize)]
struct TokenResponse {
    access_token: String,
    refresh_token: Option<String>,
    expires_in: Option<u64>,
}

async fn request_tokens(
    http: &reqwest::Client,
    service: &Service,
    form: &[(&str, &str)],
    old_refresh_token: Option<&str>,
) -> Result<Tokens, AuthError> {
    let response = http
        .post(service.authorization_server.token_endpoint.clone())
        .form(form)
        .send()
        .await?;
    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().await.unwrap_or_default();
        return Err(AuthError::TokenRefused {
            status: status.as_u16(),
            detail: format!("{status} {body}").trim().to_owned(),
        });
    }

    let token: TokenResponse = response.json().await?;
    Ok(Tokens {
        access_token: token.access_token,
        refresh_token: token.refresh_token.or_else(|| old_refresh_token.map(str::to_owned)),
        expires_at: token.expires_in.map(|seconds| unix_now() + seconds),
    })
}

fn random_token() -> String {
    URL_SAFE_NO_PAD.encode(rand::random::<[u8; 32]>())
}

/// Seconds since the Unix epoch.
#[must_use]
pub fn unix_now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs())
}

#[derive(Debug, thiserror::Error)]
#[error("cannot use the credential store: {0}")]
pub struct StoreError(String);

/// Where Custos keeps the tokens of one service.
pub trait TokenStore: Send + Sync {
    fn load(&self) -> Result<Option<Tokens>, StoreError>;
    fn save(&self, tokens: &Tokens) -> Result<(), StoreError>;
    fn clear(&self) -> Result<(), StoreError>;
}

/// The largest secret of one credential in the Windows Credential Manager. Custos uses this limit on
/// each platform.
const MAX_SECRET_BYTES: usize = 2560;

/// The bytes that the credential store keeps: the tokens as JSON. Above [`MAX_SECRET_BYTES`], the
/// access token stays out, and Custos gets a new one with the refresh token before the next upload.
fn secret(tokens: &Tokens) -> Result<Vec<u8>, StoreError> {
    let to_json = |tokens: &Tokens| serde_json::to_vec(tokens).map_err(|e| StoreError(e.to_string()));
    let json = to_json(tokens)?;
    if json.len() <= MAX_SECRET_BYTES {
        return Ok(json);
    }
    let json = to_json(&Tokens {
        access_token: String::new(),
        refresh_token: tokens.refresh_token.clone(),
        expires_at: Some(0),
    })?;
    if tokens.refresh_token.is_some() && json.len() <= MAX_SECRET_BYTES {
        Ok(json)
    } else {
        Err(StoreError(format!(
            "the tokens are larger than {MAX_SECRET_BYTES} bytes. Sign in again after the next start."
        )))
    }
}

/// The credential store of the desktop: the Secret Service on Linux, and the Credential Manager on
/// Windows. One entry for each service URL.
///
/// The calls block. Call them from a blocking thread, for example with `tokio::task::spawn_blocking`.
pub struct KeyringStore {
    account: String,
}

impl KeyringStore {
    const SERVICE: &'static str = "custos";

    #[must_use]
    pub fn new(service_url: &Url) -> Self {
        Self {
            account: service_url.to_string(),
        }
    }

    fn entry(&self) -> Result<keyring::Entry, StoreError> {
        keyring::Entry::new(Self::SERVICE, &self.account).map_err(|e| StoreError(e.to_string()))
    }
}

impl TokenStore for KeyringStore {
    fn load(&self) -> Result<Option<Tokens>, StoreError> {
        // A secret of Custos 0.1.0 is the same JSON, stored as a password, which is UTF-8 on Linux.
        match self.entry()?.get_secret() {
            Ok(json) => Ok(serde_json::from_slice(&json).ok()),
            Err(keyring::Error::NoEntry) => Ok(None),
            Err(e) => Err(StoreError(e.to_string())),
        }
    }

    fn save(&self, tokens: &Tokens) -> Result<(), StoreError> {
        self.entry()?
            .set_secret(&secret(tokens)?)
            .map_err(|e| StoreError(e.to_string()))
    }

    fn clear(&self) -> Result<(), StoreError> {
        match self.entry()?.delete_credential() {
            Ok(()) | Err(keyring::Error::NoEntry) => Ok(()),
            Err(e) => Err(StoreError(e.to_string())),
        }
    }
}

/// A store in memory, for tests.
#[derive(Default)]
pub struct MemoryStore(Mutex<Option<Tokens>>);

impl TokenStore for MemoryStore {
    fn load(&self) -> Result<Option<Tokens>, StoreError> {
        Ok(self.0.lock().map_err(|e| StoreError(e.to_string()))?.clone())
    }

    fn save(&self, tokens: &Tokens) -> Result<(), StoreError> {
        *self.0.lock().map_err(|e| StoreError(e.to_string()))? = Some(tokens.clone());
        Ok(())
    }

    fn clear(&self) -> Result<(), StoreError> {
        *self.0.lock().map_err(|e| StoreError(e.to_string()))? = None;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use wiremock::matchers::{body_string_contains, method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn discovery(base: &str) -> Service {
        crate::discovery::tests::service(base)
    }

    fn param(url: &Url, name: &str) -> String {
        url.query_pairs().find(|(k, _)| k == name).unwrap().1.into_owned()
    }

    #[test]
    fn large_tokens_are_stored_without_the_access_token() {
        let small = Tokens {
            access_token: "a".repeat(1000),
            refresh_token: Some("r".repeat(1000)),
            expires_at: Some(100),
        };
        assert_eq!(
            serde_json::from_slice::<Tokens>(&secret(&small).unwrap()).unwrap(),
            small
        );

        let large = Tokens {
            access_token: "a".repeat(2000),
            ..small.clone()
        };
        let stored: Tokens = serde_json::from_slice(&secret(&large).unwrap()).unwrap();
        assert_eq!(stored.refresh_token, large.refresh_token);
        assert!(stored.is_expiring(unix_now()));

        let no_refresh = Tokens {
            access_token: "a".repeat(3000),
            refresh_token: None,
            expires_at: None,
        };
        assert!(secret(&no_refresh).is_err());
    }

    #[test]
    fn authorize_url_has_pkce_and_the_redirect_uri() {
        let pending = begin(&discovery("https://id.example.com"));
        let url = &pending.authorize_url;

        assert_eq!(param(url, "redirect_uri"), REDIRECT_URI);
        assert_eq!(param(url, "client_id"), "custos");
        assert_eq!(param(url, "code_challenge_method"), "S256");
        assert_eq!(
            param(url, "code_challenge"),
            URL_SAFE_NO_PAD.encode(Sha256::digest(pending.verifier.as_bytes()))
        );
        assert_eq!(param(url, "state"), pending.state);
    }

    #[test]
    fn callback_with_and_without_a_slash_gives_the_code() {
        let pending = begin(&discovery("https://id.example.com"));
        for link in [
            format!("custos://oauth2callback?state={}&code=abc", pending.state),
            format!(
                "custos://oauth2callback/?state={}&session_state=x&code=abc",
                pending.state
            ),
        ] {
            assert_eq!(code_from_callback(&pending, &link).unwrap(), "abc");
        }
    }

    #[test]
    fn callback_of_a_different_login_is_refused() {
        let pending = begin(&discovery("https://id.example.com"));
        let result = code_from_callback(&pending, "custos://oauth2callback?state=other&code=abc");
        assert!(matches!(result, Err(AuthError::StateMismatch)));
    }

    #[test]
    fn callback_with_an_error_is_denied() {
        let pending = begin(&discovery("https://id.example.com"));
        let link = format!("custos://oauth2callback?state={}&error=access_denied", pending.state);
        assert!(matches!(code_from_callback(&pending, &link), Err(AuthError::Denied(e)) if e == "access_denied"));
    }

    #[test]
    fn other_links_are_not_callbacks() {
        assert!(!is_callback("custos://other?code=1"));
        assert!(!is_callback("https://oauth2callback/?code=1"));
    }

    #[tokio::test]
    async fn code_exchange_sends_the_verifier_and_keeps_the_tokens() {
        let server = MockServer::start().await;
        let d = discovery(&server.uri());
        let pending = begin(&d);
        Mock::given(method("POST"))
            .and(path("/token"))
            .and(body_string_contains("grant_type=authorization_code"))
            .and(body_string_contains(format!("code_verifier={}", pending.verifier)))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "access_token": "a1", "refresh_token": "r1", "expires_in": 300, "token_type": "Bearer"
            })))
            .mount(&server)
            .await;

        let tokens = exchange_code(&reqwest::Client::new(), &d, &pending, "abc")
            .await
            .unwrap();

        assert_eq!(tokens.access_token, "a1");
        assert_eq!(tokens.refresh_token.as_deref(), Some("r1"));
        assert!(!tokens.is_expiring(unix_now()));
    }

    #[tokio::test]
    async fn refresh_without_a_new_refresh_token_keeps_the_old_one() {
        let server = MockServer::start().await;
        Mock::given(method("POST"))
            .and(path("/token"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({ "access_token": "a2" })))
            .mount(&server)
            .await;

        let tokens = refresh(&reqwest::Client::new(), &discovery(&server.uri()), "r1")
            .await
            .unwrap();

        assert_eq!(tokens.refresh_token.as_deref(), Some("r1"));
    }

    #[tokio::test]
    async fn refused_refresh_is_an_error() {
        let server = MockServer::start().await;
        Mock::given(method("POST"))
            .respond_with(ResponseTemplate::new(400).set_body_string(r#"{"error":"invalid_grant"}"#))
            .mount(&server)
            .await;

        let result = refresh(&reqwest::Client::new(), &discovery(&server.uri()), "r1").await;

        let Err(e) = result else { panic!() };
        assert!(e.is_refusal());
        assert!(e.to_string().contains("invalid_grant"), "{e}");
    }

    #[tokio::test]
    async fn error_of_the_token_endpoint_is_not_a_refusal() {
        let server = MockServer::start().await;
        Mock::given(method("POST"))
            .respond_with(ResponseTemplate::new(503))
            .mount(&server)
            .await;

        let result = refresh(&reqwest::Client::new(), &discovery(&server.uri()), "r1").await;

        assert!(!result.unwrap_err().is_refusal());
    }
}
