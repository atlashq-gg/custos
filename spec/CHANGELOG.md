# Changelog

## v0

First version. It has no minor or patch versions.

- Discovery document at `/.well-known/custos-configuration`. It links to the OpenID configuration of the authorization server.
- Authentication with Authorization Code, PKCE, and the redirect URI `custos://oauth2callback`.
- Upload of the full map data file as `application/octet-stream`.
- Upload result document, marked unstable.
- Upload response `403`: the service does not accept uploads from the user.
- The example `client_id` in `01-discovery.md` uses the path of the reference server, `/.well-known/custos-client`.
