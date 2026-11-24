# Changelog

No release yet. The first release is 0.1.0 ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Unreleased

- The stream of `spec/05-stream.md` at `GET /api/v1/stream`, with `stream_endpoint` in the discovery document. `optio` reads the events of each source from the broker. It applies their policy with the roles of each member from Discord. It replays the events of the last 15 minutes after `Last-Event-ID`.
- The query of `spec/05-stream.md` at `POST /api/v1/query`, with `query_endpoint` in the discovery document. A model of Anthropic answers with the read tools of the MCP services. The answer comes on the open streams of the user.
- A system prompt in the configuration, with an example in `docs/system-prompt.example.txt`, and a prompt for each guild.
- `/optioadmin settings` in Discord, for the administrators of a guild. It changes the prompt of the guild.
- A PostgreSQL database for the settings of the guilds.
