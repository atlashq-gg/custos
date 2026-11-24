# Changelog

## v0

First version. It has no minor or patch versions.

- Discovery document at `/.well-known/custos-configuration`. It links to the OpenID configuration of the authorization server.
- Authentication with Authorization Code, PKCE, and the redirect URI `custos://oauth2callback`.
- Upload of the full map data file as `application/octet-stream`.
- Upload result document, marked unstable.
- Upload response `403`: the service does not accept uploads from the user.
- The example `client_id` in `01-discovery.md` uses the path of the reference server, `/.well-known/custos-client`.
- Optional stream and query in `05-stream.md`, marked unstable: the discovery members `stream_endpoint` and `query_endpoint`, the message, the query, and the answer to a confirmation.
- The messages of a query go only to the client that sent the query, and only that client can answer a confirmation.
