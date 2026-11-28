# Changelog

## v0

First version. It has no minor or patch versions.

- Discovery document at `/.well-known/custos-configuration`. It links to the OpenID configuration of the authorization server.
- Authentication with Authorization Code, PKCE, and the redirect URI `custos://oauth2callback`.
- Upload of the full map data file as `application/octet-stream`.
- Upload result document, marked unstable.
- Upload response `403`: the service does not accept uploads from the user.
- The example `client_id` in `01-discovery.md` uses the path of the reference server, `/.well-known/custos-client`.
- Optional stream in `05-stream.md`, marked unstable: the discovery member `stream_endpoint`, the message, the position of the stream, and a random part of the wait before Custos connects again.
- The required discovery member `features_supported` lists the features `upload`, `stream`, and `query`. In `v0`, it contains `upload`. Custos uses a feature only when the list names it.
- Upload interval: Custos ignores the changes of the map data file during a start delay after it starts to watch the file, and sends a change only after a delay with no further change.
- Optional query in `06-query.md`, marked unstable: the discovery member `query_endpoint`, the query with a reference from the client, the actions `accept`, `refuse`, and `stop`, the states of a query, and the stream message of the kind `query`.
- Stream message of the kind `notice`: a note of the service to the user.
- Query response `409` for a full conversation, with the problem type `urn:custos:problem:conversation-full`. The query responses `403` and `429` also cover the access to the query and the limits of the service.
