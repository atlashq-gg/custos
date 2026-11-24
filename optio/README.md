# optio

`optio` is the service that Custos calls ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). It implements the contract of [spec/](../spec/README.md): the discovery document, the upload, the stream, and the query. It calls the domain services, for example the [server](../server/README.md), with a token of the user for each service.

The work is in progress. The roadmap is [#48](https://github.com/atlashq-gg/custos/issues/48). This version serves the discovery document and the Client ID Metadata Document of Custos, relays the upload, and sends the events of the domain services on the stream. The query comes later.

## Run

Requirements: .NET SDK 10 and Docker.

```sh
dotnet run --project Optio.AppHost
```

The AppHost starts the full system:

| Resource | URL | Content |
|---|---|---|
| optio | `http://localhost:5090` | The service URL for Custos. |
| server | `http://localhost:5080` | The domain service of the guild records ([server/README.md](../server/README.md)). |
| PostgreSQL 18 | (dashboard) | The database of the server. The data lasts for one run. |
| RabbitMQ | (dashboard) | The event broker, with the users, the exchange, and the stream of [rabbitmq/definitions.json](rabbitmq/definitions.json). The server publishes its events to it. |
| Keycloak | `http://localhost:8180` | The realm `custos` from [server/keycloak/custos-realm.json](../server/keycloak/custos-realm.json), with the user `dev`, password `dev`. |
| Dashboard | `http://localhost:15180` | The Aspire dashboard. The login URL is in the console output. |

The server needs a Discord bot token in the user secrets of the AppHost ([server/README.md](../server/README.md#run)). `optio` needs a Discord bot token for the stream, in the parameter `optio-discord-token`. In development, it can be the token of the bot of the server. The AppHost makes a new secret for the client `optio` on each start.

## Endpoints

| Endpoint | Content |
|---|---|
| `GET /.well-known/custos-configuration` | The discovery document ([spec](../spec/01-discovery.md)). |
| `GET /.well-known/custos-client` | The Client ID Metadata Document (CIMD) of Custos. Its URL is the client ID. |
| `POST /api/v1/uploads` | The upload ([spec](../spec/03-upload.md)), relayed to the upload API of the server. |
| `GET /api/v1/stream` | The stream ([spec](../spec/05-stream.md)). Only when the stream is configured. |
| `GET /health/alive`, `GET /health/ready` | The health checks. |

## Upload

1. Custos logs in with the scopes of the discovery document. The scope `custos:client` gives its token the audience `optio`.
2. `optio` accepts a token with the audience `optio` and the client of Custos in `azp`.
3. `optio` examines the content type (`415`) and the size (`413`).
4. `optio` exchanges the token for a token of the server with the scope `custos:write` (RFC 8693). It keeps the exchanged token in memory until 30 seconds before it expires.
5. `optio` sends the body as a stream to the upload API of the server. It gives back the status, the body, and `Retry-After` of the server with no change.

When the authorization server refuses the exchange, or the server does not answer, the upload gets `503`. The server applies the upload interval and the Discord roles.

## Stream

The stream sends the events of the domain services to Custos ([spec](../spec/05-stream.md), [ADR 0007](../docs/adr/0007-a-broker-carries-domain-events.md)). It is on when the configuration has the broker, one source or more, and the Discord token. Then the discovery document has `stream_endpoint`.

1. `optio` reads the stream `events.<source>.stream` of each source from the broker. At the start, it attaches at the start of the replay window. After a lost connection, it attaches after its last offset.
2. `optio` keeps the events of the replay window in memory.
3. A stream opens with a token of Custos. The guild is the claim `active_guild`, and the user is the claim `discord_id`.
4. `optio` gets the roles of that member from Discord through its own bot, with the intent `GUILD_MEMBERS`. It follows only the members with an open stream.
5. `optio` sends each event whose policy the member matches ([docs/producers.md](docs/producers.md#policy)), with the roles of now. A role change applies in less than 1 second.
6. The SSE `id` holds the offset of each source. With `Last-Event-ID`, the stream first sends the events of the window after that `id`.

| Answer | Cause |
|---|---|
| `403` | The token names no guild or user, or the user is not a member of the guild. |
| `429` | The user has `MaxStreamsPerUser` open streams. `Retry-After` is 60 seconds. |
| `503` | Discord did not answer in 10 seconds. |

- The stream sends a comment each 15 seconds with no event, and it ends when the token expires.
- When the bot is not in the guild, the stream sends one `error` message and stays open.
- When Discord does not know a guild yet, `optio` holds its events until the guild is ready.

## Producers

A domain service sends events to `optio` through a RabbitMQ broker. Each event holds the text for the user and the policy that tells who can read it. The broker keeps the events of each producer in a stream, from which `optio` replays. The contract for such a producer is [docs/producers.md](docs/producers.md), with the schema [schemas/event.schema.json](schemas/event.schema.json).

The development definitions of the broker are [rabbitmq/definitions.json](rabbitmq/definitions.json). They hold the exchange and the stream of the server, and the users `custos` and `optio`, each with the password of its name.

## Configuration

The section `Optio` in `appsettings.json` or environment variables (`Optio__PublicBaseUrl`). A secret can be a file in `/run/secrets`, for example `/run/secrets/Optio__OptioClientSecret`.

| Key | Default | Content |
|---|---|---|
| `PublicBaseUrl` | (required) | The origin of `optio` as Custos sees it. |
| `Authority` | (required) | The issuer URL of the authorization server. |
| `OpenIdConfiguration` | `{Authority}/.well-known/openid-configuration` | The metadata document of the authorization server. |
| `CustosClientId` | `{PublicBaseUrl}/.well-known/custos-client` | The client ID of Custos. |
| `OptioClientId` | `optio` | The client ID of `optio`, and the audience of a token of Custos. |
| `OptioClientSecret` | (required) | The secret of the client `optio`. |
| `UploadApiUrl` | (required) | The upload API of the server, for example `http://server:8080/api/v1/uploads`. |
| `UploadScope` | `custos:write` | The scope of the token for the upload API. |
| `ServiceName` | `optio` | The name that Custos shows. |
| `ServiceDocumentation` | (none) | A page for humans about the service. |
| `Scopes` | `["openid", "custos:client"]` | The scopes that Custos requests. |
| `UploadMaxBytes` | `4194304` | The largest upload body. It must be equal to the value of the server. |
| `UploadMinIntervalSeconds` | `60` | The upload interval in the discovery document. It must be equal to the value of the server. |
| `Sources` | `["custos"]` | The sources of events. `optio` reads the stream `events.<source>.stream` of each. |
| `ReplayWindowMinutes` | `15` | How long `optio` keeps the events for a replay. |
| `MaxStreamsPerUser` | `5` | The open streams of one user. |
| `DiscordToken` | (none) | The token of the Discord bot of `optio`. Without it, `optio` has no stream. |
| `DevelopmentGuildId` | (none) | The guild of a stream whose token has no claim `active_guild`. Only in the environment `Development`, as in the server. `1236389163276959888` in development. |
| `DevelopmentDiscordUserId` | (none) | The user of a stream whose token has no claim `discord_id`. Only in the environment `Development`. |

The connection string of the broker is `ConnectionStrings:events` (`ConnectionStrings__events`), an AMQP URI with the user `optio`, for example `amqp://optio:...@broker:5672`. Without it, `optio` has no stream.

## Health and telemetry

| Endpoint | Content |
|---|---|
| `GET /health/alive` | `Healthy` while the process runs. |
| `GET /health/ready` | `Healthy` when `optio` can serve requests. |

- `optio --version` prints the version.
- `optio --healthcheck` asks `/health/alive` on this machine. The exit code is 0 for a healthy service. The `HEALTHCHECK` of the image uses it.
- `optio` exports traces, metrics, and logs with OTLP when the environment has `OTEL_EXPORTER_OTLP_ENDPOINT`. Its activity source and its meter have the name `Optio`.

| Metric | Content |
|---|---|
| `optio.events` | The events from the broker, by `outcome`: `delivered`, `no_stream` (no open stream can read it), `no_bot` (the bot is not in the guild), or `ignored` (not valid, or an unknown policy version). |
| `optio.events.age` | The age of each event when `optio` reads it, by `source`. A high age shows that `optio` is behind the end of the stream. |
| `optio.events.offset` | The last offset that `optio` read, by `source`. |
| `optio.streams` | The open streams. |
| `optio.stream.dropped` | The events that a stream dropped, because its client did not read them in time. |

## Format

```sh
dotnet tool restore
dotnet csharpier format .
```

CSharpier does not wrap comments and strings. CI fails on a line of more than 100 characters.

## Tests

```sh
dotnet test Optio.slnx
```

## Container

The image is `ghcr.io/atlashq-gg/custos-optio`. The container serves HTTP on port 8080.

| Tag | Source |
|---|---|
| `X.Y.Z`, `X.Y` | The Git tag `optio-X.Y.Z`. |
| `unstable` | Each push to `main` that changes `optio/`. |
| `sha-<commit>` | Each push to `main` that changes `optio/`. |

```sh
docker build -t custos-optio .
docker run --rm -p 8080:8080 custos-optio
```

## License

AGPL-3.0-or-later ([LICENSE](LICENSE)).
