# Changelog

No release yet. The first release is 0.1.0 ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Unreleased

- The discovery document and the Client ID Metadata Document of Custos, moved from the server. The client ID of Custos is a URL of Optio.
- The upload of `spec/03-upload.md`. Optio exchanges the token of Custos for a token of the server (RFC 8693), and relays the body as a stream to the upload API of the server.
- The stream of `spec/05-stream.md` at `GET /api/v1/stream`, with `stream_endpoint` in the discovery document. Optio reads the events of each source from the broker. It applies their policy with the roles of each member from Discord. It replays the events of the last 15 minutes after `Last-Event-ID`.
- The query of `spec/05-stream.md` at `POST /api/v1/query`, with `query_endpoint` in the discovery document. A model of Anthropic answers with the tools of the MCP services. A query belongs to the client that sent it, by the claim `sid`, and its messages go only to that client.
- A system prompt in the configuration, with an example in `docs/system-prompt.example.txt`, and a prompt for each guild.
- `/optioadmin settings` in Discord, for the administrators of a guild. It changes the prompt of the guild.
- A PostgreSQL database for the settings of the guilds.
- The write tools of the MCP services, with a `confirm` message and the answer of the user. The model writes the question, and code makes sure that it shows each short value of the change. After the user accepts, the model reads the result and writes the answer. A write tool with its own `confirm` argument gets `true` from code.
- More than one MCP service. When two services have a tool with the same name, the model sees both with the name of their service.
- One audit line in the log and the counter `optio.tool.calls` for each tool call.
- `/health/ready` checks the database.
- The producer contract in `docs/producers.md`, and the broker definitions of development in `rabbitmq/definitions.json`.
