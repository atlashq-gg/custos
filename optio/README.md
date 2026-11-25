# optio

`optio` is the service that Custos calls ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). It implements the contract of [spec/](../spec/README.md): the discovery document, the upload, the stream, and the query. It calls the domain services, for example the [server](../server/README.md), with a token of the user for each service.

`optio` serves the discovery document and the Client ID Metadata Document of Custos, relays the upload, sends the events of the domain services on the stream, and answers a query with a model and the tools of the MCP services. A write tool waits for the confirmation of the user.

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
| PostgreSQL 18 | (dashboard) | The databases of the server and of `optio`. The data lasts for one run. |
| RabbitMQ | (dashboard) | The event broker, with the users, the exchange, and the stream of [rabbitmq/definitions.json](rabbitmq/definitions.json). The server publishes its events to it. |
| Keycloak | `http://localhost:8180` | The realm `custos` from [server/keycloak/custos-realm.json](../server/keycloak/custos-realm.json), with the user `dev`, password `dev`. |
| Dashboard | `http://localhost:15180` | The Aspire dashboard. The login URL is in the console output. |

The server needs a Discord bot token in the user secrets of the AppHost ([server/README.md](../server/README.md#run)). `optio` needs a Discord bot token for the stream, in the parameter `optio-discord-token`. Use a second Discord application, not the application of the server: each service registers its commands when it starts, and a registration replaces all the commands of the application. The query needs a key of the model provider in the parameter `optio-model-key`, and uses the example system prompt [docs/system-prompt.example.txt](docs/system-prompt.example.txt). The AppHost makes a new secret for the client `optio` on each start.

## Endpoints

| Endpoint | Content |
|---|---|
| `GET /.well-known/custos-configuration` | The discovery document ([spec](../spec/01-discovery.md)). |
| `GET /.well-known/custos-client` | The Client ID Metadata Document (CIMD) of Custos. Its URL is the client ID. |
| `POST /api/v1/uploads` | The upload ([spec](../spec/03-upload.md)), relayed to the upload API of the server. |
| `GET /api/v1/stream` | The stream ([spec](../spec/05-stream.md)). Only when the stream is configured. |
| `POST /api/v1/query` | The query ([spec](../spec/05-stream.md#query)). Only when the query is configured. |
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

## Query

A query is a question of the user in plain language ([spec](../spec/05-stream.md#query)). A model answers it with the tools of the MCP services. The query is on when the configuration has the stream, the database, the model key, the system prompt, and one MCP service or more. Then the discovery document has `query_endpoint`.

1. Custos sends the query with its token. The client needs an open stream (`409` if not).
2. `optio` answers `202` with the reference of the query, and runs the query in the background.
3. For each MCP service, `optio` gets a token of the user with the read scope by exchange, and the service lists its read tools. With a `WriteScope`, `optio` also lists the tools with the read and write scopes. A tool that is only in that list is a write tool.
4. The model gets the system prompt, the guild of `active_guild`, the prompt of the guild, the query, and the tools. Each query starts with no earlier conversation.
5. Code sets the argument `guild_id` of each tool call to `active_guild`. The model does not see this argument. The model sees no token.
6. When two services have a tool with the same name, the model sees both tools as `{service}_{tool}`, for example `custos_list_guilds`. Other names do not change.
7. `optio` sends one `progress` message for each tool call, then one `answer` or one `error`. Each message has the reference of the query.

A query belongs to the client that sent it: the session of the client at the authorization server, in the claim `sid`. The messages of the query go only to the streams of that client.

### Write tools

1. When the model calls a write tool, the loop stops. No tool of that turn runs yet.
2. `optio` sends one `confirm` message with the reference of the query. It keeps the confirmation and the conversation of the query in memory for 60 seconds. One confirmation holds all the write calls of the turn.
3. The client that sent the query answers with `{"confirm": "<reference>", "accept": true}` or `false`. Code reads the answer, not the model. An answer from another client, or after the 60 seconds, gets `409`.
4. With `accept` `true`, `optio` calls the tools of the turn. For a write tool, it gets a token with the write scope by exchange from the token of the answer. Then the loop continues: the model reads the results and writes the answer. It says that the action is done, and refers to the result. A new write call asks the user again. Each part has the limits of a query, and the tool calls of all parts count together.
5. With `accept` `false`, `optio` removes the line of the confirmation: a message with the reference and no text.
6. A new query of the client ends its open confirmation, and removes its line.
7. When a write tool has a boolean argument `confirm`, code sets it to `true`, and the model does not see it. The confirmation of `optio` replaces the confirmation of the service.

The text of the `confirm` message:

- `optio` adds the argument `confirm_text` to each write tool, and removes it before the call. The model writes the question in it. The description of the argument tells the model that the question shows on a screen 50 characters wide. The model must leave out each word that is not necessary.
- The form is `{Verb} {Object}? "value"`, with `and "value"` for each other value. A new value shows the new value. A removal shows the current value, from a tool result. A long text does not show: the question names the action. Examples: `Set Label? "Main Depot"`, `Set Labels? "Main Depot" and "North Bunker"`, `Remove Label? "Old Depot"`, `Post Progress Log?`.
- Before a removal, the example system prompt tells the model to read the current value, for example with `find_structures`.
- `optio` uses the question of the model only when all of these are true:
  - It is one line of 120 characters at most.
  - It holds each text value of the calls of 40 characters or less, in any case. A longer text value, a boolean, or a number does not need to be in it.
  - Each quoted text in it has a source: a value of the calls, or a text in a tool result of the query. A quoted text that ends in `…` must start a source.
- Code puts the source of each quoted text in the question, so that the user sees the value with its real case.
- Else code makes the text from the title of each tool and the values, for example `Set the label of a structure? "Main Depot" and "North Bunker"`. It cuts a value after 40 characters with `…`. An absent value shows as `(none)`.

The check makes sure that the user sees each value of the change, and that each quoted text has a source. It cannot make sure that the verb of the question is correct, or that a current value belongs to the structure of the call.

### Limits

| Limit | Value |
|---|---|
| Tool calls of one query | 8 |
| Time of one query | 60 seconds |
| Running queries of one user | 1 |
| Queries of one user in one hour | `MaxQueriesPerHour` |
| Time of one confirmation | 60 seconds |

A query over a limit of the user gets `429` with `Retry-After`. An answer to a confirmation does not count in the hour. An accept while another query of the user runs gets `429`, and the confirmation stays open.

### Audit

Each tool call writes one line to the log. The line holds the user, the guild, the service, the tool, and the outcome. The outcome is `ok`, `error` (the tool gave an error), or `failed` (the call did not complete). The counter `optio.tool.calls` counts the calls with the tags `service`, `tool`, and `outcome`.

The harness calls the model only through `IModelClient`. The only implementation calls the Messages API of Anthropic with the Anthropic C# SDK.

### Prompts

- The system prompt is the configuration value `SystemPrompt`. The repository holds an example: [docs/system-prompt.example.txt](docs/system-prompt.example.txt).
- Each guild can have its own prompt, 1000 characters at most. It comes after the system prompt, in its own block.
- An administrator of a guild changes it with `/optioadmin settings` in Discord: a form shows the prompt of now. Discord hides the command from members without the permission Administrator, and `optio` examines the permission again at each use. The bot of `optio` needs the scope `applications.commands`.
- `optio` keeps the prompt of each guild in its PostgreSQL database, and writes an audit line to the log at each change.

## Add a service

`optio` can use the tools and the events of more than one domain service of the realm. A new service needs only configuration:

1. In the realm, give the client `optio` the read scope and the write scope of the service as optional client scopes. The service must accept a token of `optio` with these scopes.
2. Add an entry to `McpServices` with `Name`, `Url`, `ReadScope`, and `WriteScope`. The name is short, with letters, digits, `-`, and `_` only: it becomes a part of a tool name when two services share a tool name.
3. For the events of the service, add its name to `Sources`, and give the service a producer user in the broker ([docs/producers.md](docs/producers.md#join-as-a-new-producer)).
4. Tell the model about the service in the system prompt of the configuration.

The tools of the service must use the argument `guild_id` for the guild, so that code can set it.

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
| `ModelApiKey` | (none) | The key of the model provider. Without it, `optio` answers no query. |
| `ModelId` | `claude-haiku-5-5` | The model of the query. |
| `ModelEffort` | `medium` | The effort of the model: `low`, `medium`, `high`, `xhigh`, or `max`. |
| `SystemPrompt` | (none) | The system prompt of the query. Without it, `optio` answers no query. |
| `MaxQueriesPerHour` | `20` | The queries of one user in one hour. |
| `McpServices` | `[]` | The MCP services of the query. Each entry has `Name`, `Url` (the MCP endpoint), `ReadScope` (for example `custos:read`), and an optional `WriteScope` (for example `custos:write`). Without `WriteScope`, the query uses no write tool of the service. |

The connection string of the database is `ConnectionStrings:optio` (`ConnectionStrings__optio`). Without it, `optio` keeps no guild settings, and has no commands and no query. `optio` applies its migrations when it starts.

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
| `optio.tool.calls` | The tool calls of the queries, by `service`, `tool`, and `outcome`: `ok`, `error`, or `failed`. |

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

The tests need Docker. Three categories need more, and CI does not run them. Without their variables, they skip.

| Category | Needs | Content |
|---|---|---|
| `Discord` | `Discord__Token`, `Discord__TestUserId` | The member directory against the real Discord gateway. |
| `Feasibility` | (none) | The feasibility tests against the broker. They take about 2 minutes. |
| `Model` | `Optio__ClaudeApiKey` | The query with the real model, the example system prompt, and a small MCP server. Each run costs a few cents. |

```sh
set -a; . ./.env; set +a
dotnet test Optio.Tests --filter "TestCategory=Model"
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

## Deploy

[docs/deploy.md](../docs/deploy.md) deploys the full system: Keycloak, PostgreSQL, the broker, the server, and `optio`.

## License

AGPL-3.0-or-later ([LICENSE](LICENSE)).
