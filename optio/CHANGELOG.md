# Changelog

## 0.1.0-alpha.1

The first pre-release.

- The discovery document and the Client ID Metadata Document of Custos. The upload relay: Optio exchanges the token of Custos for a token of the server (RFC 8693), and sends the body to the upload API of the server.
- The producer contract [docs/producers.md](docs/producers.md) and its schema [schemas/event.schema.json](schemas/event.schema.json): a domain service publishes each event with its data and its access policy on NATS JetStream.
- The NATS configuration [nats/nats.conf](nats/nats.conf): the users of the producer and of Optio, with their permissions.
- The stream of `spec/05-stream.md` at `GET /api/v1/stream`, with `stream_endpoint` in the discovery document. Optio makes the stream of the events on NATS JetStream at its start. Each open stream has its own reader on the broker, applies the access policy of each event with the roles of the member from Discord, and first sends its position. Without the broker, a stream request gets `503`, and the upload still works.
- The AppHost starts NATS with `nats/nats.conf`, and gives the server and Optio their broker users.
- Telemetry: the metrics `optio.events`, `optio.events.age`, and `optio.streams`. The service name is `Optio`, unless `OTEL_SERVICE_NAME` sets another.
- [docs/deploy.md](../docs/deploy.md) deploys the full system with Docker Compose.
