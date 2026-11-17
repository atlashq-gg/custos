//! The discovery document (spec/discovery.md).

use serde::Deserialize;
use url::Url;

/// The contract versions that this version of Custos implements.
pub const SUPPORTED_VERSIONS: &[&str] = &["0.1"];

const WELL_KNOWN: &str = "/.well-known/custos-configuration";

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct Discovery {
    pub custos_versions_supported: Vec<String>,
    pub service_name: String,
    pub service_documentation: Option<Url>,
    pub upload_endpoint: Url,
    pub upload_max_bytes: u64,
    pub upload_min_interval_seconds: u64,
    pub authorization_endpoint: Url,
    pub token_endpoint: Url,
    pub revocation_endpoint: Option<Url>,
    pub client_id: String,
    pub scopes: Vec<String>,
}

#[derive(Debug, thiserror::Error)]
pub enum DiscoveryError {
    #[error("cannot get the discovery document from {url}: {source}")]
    Http { url: Url, source: reqwest::Error },
    #[error("the service supports the contract versions {offered:?}, but Custos supports only {SUPPORTED_VERSIONS:?}")]
    NoCommonVersion { offered: Vec<String> },
    #[error("the discovery document names an insecure URL: {0}")]
    Insecure(Url),
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

        let urls = [
            &self.upload_endpoint,
            &self.authorization_endpoint,
            &self.token_endpoint,
        ]
        .into_iter()
        .chain(self.revocation_endpoint.as_ref());
        for url in urls {
            if !crate::url_rules::is_allowed(url) {
                return Err(DiscoveryError::Insecure(url.clone()));
            }
        }

        Ok(self)
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

/// Gets and checks the discovery document of the service.
pub async fn fetch(http: &reqwest::Client, service_url: &Url) -> Result<Discovery, DiscoveryError> {
    let url = discovery_url(service_url);
    let error = |source| DiscoveryError::Http {
        url: url.clone(),
        source,
    };

    let discovery: Discovery = http
        .get(url.clone())
        .send()
        .await
        .and_then(reqwest::Response::error_for_status)
        .map_err(error)?
        .json()
        .await
        .map_err(error)?;

    discovery.validate()
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
            "custos_versions_supported": ["0.1"],
            "service_name": "Test",
            "upload_endpoint": format!("{base}/api/v1/uploads"),
            "upload_max_bytes": 4_194_304,
            "upload_min_interval_seconds": 60,
            "authorization_endpoint": format!("{base}/auth"),
            "token_endpoint": format!("{base}/token"),
            "client_id": "custos",
            "scopes": ["openid"],
            "a_member_from_the_future": true
        })
    }

    #[tokio::test]
    async fn document_is_fetched_and_unknown_members_are_ignored() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/.well-known/custos-configuration"))
            .respond_with(ResponseTemplate::new(200).set_body_json(document(&server.uri())))
            .mount(&server)
            .await;

        let discovery = fetch(&reqwest::Client::new(), &Url::parse(&server.uri()).unwrap())
            .await
            .unwrap();

        assert_eq!(discovery.selected_version(), Some("0.1"));
        assert_eq!(discovery.upload_min_interval_seconds, 60);
        assert_eq!(discovery.revocation_endpoint, None);
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
        let server = MockServer::start().await;
        let mut body = document(&server.uri());
        body["custos_versions_supported"] = serde_json::json!(["9.0"]);
        Mock::given(method("GET"))
            .respond_with(ResponseTemplate::new(200).set_body_json(body))
            .mount(&server)
            .await;

        let result = fetch(&reqwest::Client::new(), &Url::parse(&server.uri()).unwrap()).await;

        assert!(matches!(result, Err(DiscoveryError::NoCommonVersion { .. })));
    }

    #[tokio::test]
    async fn insecure_endpoint_is_refused() {
        let server = MockServer::start().await;
        let mut body = document(&server.uri());
        body["token_endpoint"] = serde_json::json!("http://id.example.com/token");
        Mock::given(method("GET"))
            .respond_with(ResponseTemplate::new(200).set_body_json(body))
            .mount(&server)
            .await;

        let result = fetch(&reqwest::Client::new(), &Url::parse(&server.uri()).unwrap()).await;

        assert!(matches!(result, Err(DiscoveryError::Insecure(_))));
    }
}
