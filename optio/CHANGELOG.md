# Changelog

## Unreleased

- The NATS configuration moves to [deploy/nats/nats.conf](../deploy/nats/nats.conf), and the deployment guide to [deploy/README.md](../deploy/README.md), with the files `compose.yaml` and `.env.example`. The tests and the AppHost read the realm and the NATS configuration from `deploy/`.

## 0.1.0-alpha.1

The first pre-release.

- The discovery document and the Client ID Metadata Document of Custos. The upload relay: Optio exchanges the token of Custos for a token of the server (RFC 8693), and sends the body to the upload API of the server.
- The producer contract [docs/producers.md](docs/producers.md) and its schema [schemas/event.schema.json](schemas/event.schema.json): a domain service publishes each event with its data and its access policy on NATS JetStream.
- The NATS configuration [nats/nats.conf](nats/nats.conf): the users of the producer and of Optio, with their permissions.
- The stream of `spec/05-stream.md` at `GET /api/v1/stream`, with `stream_endpoint` in the discovery document. Optio makes the stream of the events on NATS JetStream at its start. Each open stream has its own reader on the broker, applies the access policy of each event with the roles of the member from Discord, and first sends its position. Without the broker, a stream request gets `503`, and the upload still works.
- The AppHost starts NATS with `nats/nats.conf`, and gives the server and Optio their broker users.
- Telemetry: the metrics `optio.events`, `optio.events.age`, and `optio.streams`. The service name is `Optio`, unless `OTEL_SERVICE_NAME` sets another.
- [docs/deploy.md](../docs/deploy.md) deploys the full system with Docker Compose.
- The query of `spec/06-query.md` at `POST /api/v1/query`, with `query_endpoint` in the discovery document. The record of each query is in the NATS bucket `OPTIO_QUERIES`, and its messages go to the stream `OPTIO` below `queries.<guild>.<user>.<client>`. The loop calls the Claude API and the read-only tools of the MCP services, with the card of each service in the prompt. A part of a query has a budget of time and of tool calls. The conversation of a client stays in the bucket `OPTIO_CONVERSATIONS` for the follow-up window. The actions `stop`, the replacement by a new query, the limits of one open query for each user and of queries in each hour, and the close of interrupted queries at the start and the stop.
- Telemetry: the metrics `optio.queries` and `optio.tool.calls`.
- Changes: a write tool of a service with `WriteScope` waits for the decision of the user. The states `waiting` and `applying`, the actions `accept` and `refuse`, the expiry of a question after `QuestionSeconds`, the question with the rules of code, the check of each ID of a write call, and `result_unknown` after a failed change. A question continues after a restart.
- The prompt of a guild: the database of Optio (PostgreSQL, `ConnectionStrings:optio`) holds the settings of each guild, and readiness checks it. An administrator sets the prompt with `/optioadmin settings` in Discord. The query puts it last in the system prompt. The AppHost adds the database `optio`.
