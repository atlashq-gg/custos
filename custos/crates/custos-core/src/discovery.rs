//! The discovery document and the metadata of the authorization server (spec/01-discovery.md).

use serde::Deserialize;
use url::Url;

/// The contract versions that this version of Custos implements.
pub const SUPPORTED_VERSIONS: &[&str] = &["0"];

const WELL_KNOWN: &str = "/.well-known/custos-configuration";

/// The discovery document of the service.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct Discovery {
    pub custos_versions_supported: Vec<String>,
    pub service_name: String,
    pub service_documentation: Option<Url>,
    pub upload_endpoint: Url,
    pub upload_max_bytes: u64,
    pub upload_min_interval_seconds: u64,
    pub openid_configuration: Url,
    pub client_id: String,
    pub scopes: Vec<String>,
}

/// The members of the metadata of the authorization server that Custos uses.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct AuthorizationServer {
    pub authorization_endpoint: Url,
    pub token_endpoint: Url,
    pub revocation_endpoint: Option<Url>,
    #[serde(default)]
    pub code_challenge_methods_supported: Vec<String>,
}

/// A service: its discovery document and its authorization server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Service {
    pub discovery: Discovery,
    pub authorization_server: AuthorizationServer,
}

#[derive(Debug, thiserror::Error)]
pub enum DiscoveryError {
    #[error("cannot get {url}: {source}")]
    Http { url: Url, source: reqwest::Error },
    #[error("the service supports the contract versions {offered:?}, but Custos supports only {SUPPORTED_VERSIONS:?}")]
    NoCommonVersion { offered: Vec<String> },
    #[error("the service names an insecure URL: {0}")]
    Insecure(Url),
    #[error("the authorization server does not support PKCE with S256")]
    NoS256,
}

impl Discovery {
    /// The contract version that Custos and the service use: the highest version that both support.
    #[must_use]
    pub fn selected_version(&self) -> Option<&str> {
        SUPPORTED_VERSIONS
            .iter()
            .rev()
            .find(|v| self.custos_versions_supported.iter().any(|o| o == *v))
            .copied()
    }

    fn validate(self) -> Result<Self, DiscoveryError> {
        if self.selected_version().is_none() {
            return Err(DiscoveryError::NoCommonVersion {
                offered: self.custos_versions_supported,
            });
        }
        require_allowed([&self.upload_endpoint, &self.openid_configuration])?;
        Ok(self)
    }
}

impl AuthorizationServer {
    fn validate(self) -> Result<Self, DiscoveryError> {
        require_allowed(
            [&self.authorization_endpoint, &self.token_endpoint]
                .into_iter()
                .chain(self.revocation_endpoint.as_ref()),
        )?;
        if !self.code_challenge_methods_supported.is_empty()
            && !self.code_challenge_methods_supported.iter().any(|m| m == "S256")
        {
            return Err(DiscoveryError::NoS256);
        }
        Ok(self)
    }
}

fn require_allowed<'a>(urls: impl IntoIterator<Item = &'a Url>) -> Result<(), DiscoveryError> {
    match urls.into_iter().find(|url| !crate::url_rules::is_allowed(url)) {
        Some(url) => Err(DiscoveryError::Insecure(url.clone())),
        None => Ok(()),
    }
}

/// The discovery URL of a service URL: the well-known path goes between the origin and the path
/// (RFC 8414, section 3.1).
#[must_use]
pub fn discovery_url(service_url: &Url) -> Url {
    let mut url = service_url.clone();
    let path = service_url.path().trim_end_matches('/');
    url.set_path(&format!("{WELL_KNOWN}{path}"));
    url.set_query(None);
    url.set_fragment(None);
    url
}

/// The longest time of one request for the discovery. Custos waits for the discovery before it does
/// anything else, so a service that does not answer must not block it for long.
const REQUEST_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(15);

/// Gets and checks the discovery document of the service, then the metadata of the authorization server that it links.
pub async fn fetch(http: &reqwest::Client, service_url: &Url) -> Result<Service, DiscoveryError> {
    let discovery: Discovery = get_json(http, discovery_url(service_url)).await?;
    let discovery = discovery.validate()?;
    let authorization_server: AuthorizationServer = get_json(http, discovery.openid_configuration.clone()).await?;

    Ok(Service {
        discovery,
        authorization_server: authorization_server.validate()?,
    })
}

async fn get_json<T: serde::de::DeserializeOwned>(http: &reqwest::Client, url: Url) -> Result<T, DiscoveryError> {
    let error = |source| DiscoveryError::Http {
        url: url.clone(),
        source,
    };
    http.get(url.clone())
        .timeout(REQUEST_TIMEOUT)
        .send()
        .await
        .and_then(reqwest::Response::error_for_status)
        .map_err(error)?
        .json()
        .await
        .map_err(error)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    #[test]
    fn well_known_path_goes_before_the_service_path() {
        let cases = [
            (
                "https://example.com",
                "https://example.com/.well-known/custos-configuration",
            ),
            (
                "https://example.com/",
                "https://example.com/.well-known/custos-configuration",
            ),
            (
                "https://example.com/regiment/42",
                "https://example.com/.well-known/custos-configuration/regiment/42",
            ),
            (
                "http://localhost:5080/a/",
                "http://localhost:5080/.well-known/custos-configuration/a",
            ),
        ];
        for (service, expected) in cases {
            assert_eq!(discovery_url(&Url::parse(service).unwrap()).as_str(), expected);
        }
    }

    pub(crate) fn document(base: &str) -> serde_json::Value {
        serde_json::json!({
            "custos_versions_supported": ["0"],
            "service_name": "Test",
            "upload_endpoint": format!("{base}/api/v1/uploads"),
            "upload_max_bytes": 4_194_304,
            "upload_min_interval_seconds": 60,
            "openid_configuration": format!("{base}/.well-known/openid-configuration"),
            "client_id": "custos",
            "scopes": ["openid"],
            "a_member_from_the_future": true
        })
    }

    pub(crate) fn openid_configuration(base: &str) -> serde_json::Value {
        serde_json::json!({
            "issuer": base,
            "authorization_endpoint": format!("{base}/auth"),
            "token_endpoint": format!("{base}/token"),
            "code_challenge_methods_supported": ["plain", "S256"],
            "jwks_uri": format!("{base}/certs")
        })
    }

    /// The service of the tests, built without HTTP.
    pub(crate) fn service(base: &str) -> Service {
        Service {
            discovery: serde_json::from_value(document(base)).unwrap(),
            authorization_server: serde_json::from_value(openid_configuration(base)).unwrap(),
        }
    }

    /// Serves both documents. Returns the server.
    pub(crate) async fn mock_service(
        discovery: impl FnOnce(&mut serde_json::Value),
        openid: impl FnOnce(&mut serde_json::Value),
    ) -> MockServer {
        let server = MockServer::start().await;
        let (mut d, mut o) = (document(&server.uri()), openid_configuration(&server.uri()));
        discovery(&mut d);
        openid(&mut o);
        Mock::given(method("GET"))
            .and(path("/.well-known/custos-configuration"))
            .respond_with(ResponseTemplate::new(200).set_body_json(d))
            .mount(&server)
            .await;
        Mock::given(method("GET"))
            .and(path("/.well-known/openid-configuration"))
            .respond_with(ResponseTemplate::new(200).set_body_json(o))
            .mount(&server)
            .await;
        server
    }

    async fn fetch_from(server: &MockServer) -> Result<Service, DiscoveryError> {
        fetch(&reqwest::Client::new(), &Url::parse(&server.uri()).unwrap()).await
    }

    #[tokio::test]
    async fn both_documents_are_fetched_and_unknown_members_are_ignored() {
        let server = mock_service(|_| {}, |_| {}).await;

        let service = fetch_from(&server).await.unwrap();

        assert_eq!(service.discovery.selected_version(), Some("0"));
        assert_eq!(service.discovery.upload_min_interval_seconds, 60);
        assert_eq!(service.authorization_server.token_endpoint.path(), "/token");
        assert_eq!(service.authorization_server.revocation_endpoint, None);
    }

    #[test]
    fn test_document_has_every_member_that_the_spec_requires() {
        // The fixture of these tests must itself be a valid document, or the tests prove nothing.
        let schema: serde_json::Value =
            serde_json::from_str(include_str!("../../../../spec/schemas/discovery.schema.json")).unwrap();
        for member in schema["required"].as_array().unwrap() {
            assert!(
                document("http://localhost").get(member.as_str().unwrap()).is_some(),
                "{member}"
            );
        }
    }

    #[tokio::test]
    async fn service_without_a_common_version_is_refused() {
        let server = mock_service(|d| d["custos_versions_supported"] = serde_json::json!(["9"]), |_| {}).await;
        assert!(matches!(
            fetch_from(&server).await,
            Err(DiscoveryError::NoCommonVersion { .. })
        ));
    }

    #[tokio::test]
    async fn insecure_endpoint_in_the_openid_configuration_is_refused() {
        let server = mock_service(|_| {}, |o| o["token_endpoint"] = "http://id.example.com/token".into()).await;
        assert!(matches!(fetch_from(&server).await, Err(DiscoveryError::Insecure(_))));
    }

    #[tokio::test]
    async fn authorization_server_without_s256_is_refused() {
        let server = mock_service(
            |_| {},
            |o| o["code_challenge_methods_supported"] = serde_json::json!(["plain"]),
        )
        .await;
        assert!(matches!(fetch_from(&server).await, Err(DiscoveryError::NoS256)));
    }
}
