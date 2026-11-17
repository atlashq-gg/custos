//! The transport rule of the specification: HTTPS, or HTTP to a loopback host.

use url::{Host, Url};

/// True when Custos can send data to `url`: the scheme is `https`, or the scheme is `http` and the host
/// is `localhost` or a loopback address.
#[must_use]
pub fn is_allowed(url: &Url) -> bool {
    match url.scheme() {
        "https" => true,
        "http" => match url.host() {
            Some(Host::Domain(domain)) => domain.eq_ignore_ascii_case("localhost"),
            Some(Host::Ipv4(ip)) => ip.is_loopback(),
            Some(Host::Ipv6(ip)) => ip.is_loopback(),
            None => false,
        },
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn https_and_loopback_http_are_allowed() {
        for url in [
            "https://example.com",
            "http://localhost:5080",
            "http://127.0.0.1",
            "http://[::1]:80",
        ] {
            assert!(is_allowed(&Url::parse(url).unwrap()), "{url}");
        }
    }

    #[test]
    fn other_http_and_other_schemes_are_refused() {
        for url in [
            "http://example.com",
            "http://10.0.0.1",
            "ftp://localhost",
            "custos://oauth2callback",
        ] {
            assert!(!is_allowed(&Url::parse(url).unwrap()), "{url}");
        }
    }
}
