# Changelog

No release yet. The first release is 0.1.0 ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Unreleased

- The stream of `spec/05-stream.md` at `GET /api/v1/stream`, with `stream_endpoint` in the discovery document. `optio` reads the events of each source from the broker. It applies their policy with the roles of each member from Discord. It replays the events of the last 15 minutes after `Last-Event-ID`.
