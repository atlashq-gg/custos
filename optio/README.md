# Optio

Optio is the service that Custos calls ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). It implements the contract of [spec/](../spec/README.md): the discovery document, the upload, the stream, and the query. It calls the domain services, for example the [server](../server/README.md), with a token of the user for each service.

Optio serves the discovery document and the Client ID Metadata Document of Custos, and relays the upload. It sends the events of the domain services on the stream. It answers the questions of the user with a model and the MCP tools of the domain services.

## Run

Requirements: .NET SDK 10 and Docker. The AppHost of the full system starts Optio on `http://localhost:5090`, with the Server, Keycloak, PostgreSQL, and the NATS broker ([deploy/system/README.md](../deploy/system/README.md)):

```sh
dotnet run --project ../deploy/system/Custos.AppHost
```

Optio has the stream only with the parameter `optio-discord-token`, and the query only with the parameter `claude-api-key`.

## Endpoints

| Endpoint | Content |
|---|---|
| `GET /.well-known/custos-configuration` | The discovery document ([spec](../spec/01-discovery.md)). |
| `GET /.well-known/custos-client` | The Client ID Metadata Document (CIMD) of Custos. Its URL is the client ID. |
| `POST /api/v1/uploads` | The upload ([spec](../spec/03-upload.md)), relayed to the upload API of the server. |
| `GET /api/v1/stream` | The stream ([spec](../spec/05-stream.md)). Only when the stream is configured. |
| `POST /api/v1/query` | The query ([spec](../spec/06-query.md)). Only when the query is configured. |
| `GET /health/alive`, `GET /health/ready` | The health checks. |

## Upload

1. Custos logs in with the scopes of the discovery document. The scope `custos:client` gives its token the audience `optio`.
2. Optio accepts a token with the audience `optio` and the client of Custos in `azp`.
3. Optio examines the content type (`415`) and the size (`413`).
4. Optio exchanges the token for a token of the server with the scope `custos:write` (RFC 8693). It keeps the exchanged token in memory until 30 seconds before it expires.
5. Optio sends the body as a stream to the upload API of the server. It gives back the status, the body, and `Retry-After` of the server with no change.

When the authorization server refuses the exchange, or the server does not answer, the upload gets `503`. The server applies the upload interval and the Discord roles.

## Stream

The stream sends the events of the domain services to Custos ([spec](../spec/05-stream.md), [ADR 0007](../docs/adr/0007-a-broker-carries-domain-events.md)). It is on when the configuration has the broker and the Discord token. Then the discovery document lists `stream` in `features_supported`, and has `stream_endpoint`.

1. When Optio starts, it makes the stream `OPTIO` on the broker, or sets its settings again. The broker keeps the events for the replay window, and removes each event at its age.
2. A stream opens with a token of Custos. The guild is the claim `active_guild`, and the user is the claim `discord_id`.
3. Optio gets the roles of that member from Discord through its own bot, with the intent `GUILD_MEMBERS`. It follows only the members with an open stream.
4. The stream first sends its position: an SSE `id` with no `data`. The `id` holds the creation time of the broker stream and the sequence, for example `1764090192512-142`.
5. Each open stream has its own reader on the broker, from its position: after `Last-Event-ID`, or from now. A position of another broker stream, or after its end, starts from now.
6. Optio sends each event whose policy the member matches ([docs/producers.md](docs/producers.md#policy)), with the roles of now. A role change applies in less than 1 second.

| Answer | Cause |
|---|---|
| `403` | The token names no guild or user, or the user is not a member of the guild. |
| `429` | The user has `MaxStreamsPerUser` open streams. `Retry-After` is 60 seconds. |
| `503` | The broker is not available, or Discord did not answer in 10 seconds. |

- The stream sends a comment each 15 seconds with no event, and it ends when the token expires.
- When the bot is not in the guild, the stream sends one `error` message and stays open.
- When Discord does not know a guild yet, the reader waits until the guild is ready.
- When the reader fails, for example because an operator removed the broker stream, the stream ends. At the next connection, Optio makes the broker stream again.
- Optio keeps no events in memory. It holds only the count of open streams of each user.

## Query

The query answers a question of the user in plain language, on the stream ([spec](../spec/06-query.md)). It is on when the stream is on and the configuration has `ClaudeApiKey`, `SystemPrompt`, and one item of `McpServices` or more. Then the discovery document lists `query` in `features_supported`, and has `query_endpoint`.

1. Custos sends `POST /api/v1/query` with a reference that it makes and the question. The client is the claim `sid` of the token, so a token with no `sid` gets `403`.
2. Optio writes the record of the query to the bucket `OPTIO_QUERIES`, and responds `202`. Each change of state is a compare-and-set on the record, and the winner publishes the message of the change below `queries.<guild>.<user>.<client>` in the stream `OPTIO`. Thus the messages replay with the events, and only the streams of the client get them.
3. The model gets the system prompt, a section for each MCP service with its card, `OperatorText`, the guild, and the tools of the services. Each tool name is `{service}_{tool}`. Code sets the argument `guild_id`. A tool that the service does not mark read-only is a write tool, and only a service with `WriteScope` gives its write tools.
4. Each tool call publishes a status, for example `Custos: Find items`. A failure of a tool goes to the model as a tool error. A service that is not available is not in the tool list, and the prompt states it.
5. A part has a budget of `PartBudgetSeconds` and of `MaxToolCallsPerPart` tool calls. At the end of a budget, the model gets one last step in which it cannot call a tool.
6. When the model calls a write tool, no call of that step runs. The query goes to `waiting` with the question, for `QuestionSeconds`. After `accept`, it goes to `applying`: Optio gets a token of the write scope from the token of the decision, and runs the calls of the step. The results go to the model, and the loop continues in a new part. After `refuse` or the end of the question, the query closes, and nothing changes.
7. The query closes with the answer. The question, the tool calls, the results, and the answer go to the conversation of the client in the bucket `OPTIO_CONVERSATIONS`. A query in the follow-up window continues the conversation, unless it has `new_conversation`.

| Answer | Cause |
|---|---|
| `403` | The token names no guild, user, or session. |
| `404` | An action for a reference that is not the last query of the client. |
| `409` | Another client of the user has an open query, the state of the query does not permit the action, or a change of the last query is still in progress after 10 seconds. |
| `429` | The user started `MaxQueriesPerHour` queries in the last hour. `Retry-After` gives the wait. |
| `503` | The broker is not available. |

- A new query of a client closes its open query with the cause `replaced`. `stop` closes it with `stopped`.
- A stream that opens sends the state of the open query of its client after the position.
- Optio keeps the card and the tool list of each service for `CatalogMinutes`, and gets them again with the token of a query. It gets a token of the user for the read scope of each service by exchange.
- When a new tool list has a tool that the earlier list did not have, Optio uses the tool at once and writes a warning: the [evaluation set](#evaluation) did not run with it.
- At its start, Optio closes each query that an earlier instance left open, with the cause `interrupted`. At a normal stop, it closes its running queries the same way.
- The limit of queries in each hour is in memory. A restart of Optio resets it.
- Each tool call writes an audit line to the log.
- The text of the guild ([settings](#settings-of-a-guild)) comes last in the system prompt, in its own block. When Optio has a database but cannot read it, the query fails.
- The question: the model writes it in the argument `confirm_text` of the write tool. Code uses it only when it is one line of 120 characters at most, holds each text value of 40 characters or less, and each quoted text comes from the call or from a tool result of the query. Code then gives each quoted text the case of its source. Else code makes the question from the tool title and the values, for example `Set the label of a structure? "Main Depot"`. Code sets the argument `confirm` of a write tool to true.
- Each ID argument of a write call must come from a tool result of the query. Else no call of the step runs, and the model gets a tool error.
- A failure of a service during `applying` closes the query with `result_unknown`. A new query of the client waits up to 10 seconds for a change in progress.
- A question continues after a restart of Optio: the record holds the step. A running query closes with `interrupted`, or with `result_unknown` during a change.

## Token use

Each request to the model gives its tokens: the input, the output, the input that the cache gave, and the input that went into the cache. Optio adds them to the metric `optio.model.tokens`. With its database, Optio also writes a row for each request to the table `query_usage`, for the audit. The row holds no text of the query. Its columns are:

- the time, the guild, and the user
- a hash of the session, and the reference of the query
- the model, and the four counts
- the external reference: the ID of the response message at the provider of the model, for example `msg_...` of Anthropic

- A query that fails or stops keeps the rows of its requests.
- When the database does not take a row, the query continues. Optio writes an error to the log and adds to `optio.usage.failures`.
- Optio removes the rows older than `UsageRetentionDays` when it starts, and then each day.

## Settings of a guild

The database of Optio holds one settings document for each guild. Its members are in PascalCase, and each ID is a string. A guild without a document has the default.

```json
{
  "SchemaVersion": 1,
  "Access": { "QueryRoleIds": ["500000000000000001"] },
  "Prompts": {
    "Guild": "Answer in French.",
    "Roles": { "500000000000000001": "The member leads a logistics crew." }
  },
  "Limits": {
    "Roles": { "500000000000000001": 50000 },
    "Users": { "400000000000000002": 10000 }
  }
}
```

| Member | Content |
|---|---|
| `Access.QueryRoleIds` | The roles that can ask Optio a question, 25 at most. Empty: an administrator only. |
| `Prompts.Guild` | The text of the guild for the system prompt, 1000 characters at most. |
| `Prompts.Roles` | The text of each role for the system prompt, 500 characters at most, 25 roles at most. |
| `Limits.Roles` | The tokens in one hour for a member with the role, 25 roles at most. |
| `Limits.Users` | The tokens in one hour for one member. It replaces the limits of the roles of the member. |

The query uses only `Prompts.Guild` now. Optio keeps the other members.

An administrator of the guild changes the settings with `/optioadmin` in Discord. Each reply is ephemeral.

| Command | Result |
|---|---|
| `settings view` | Shows the document, and attaches it as a file. |
| `settings update [file]` | Replaces the document from a file (`MaxSettingsFileBytes` at most) or from a form. The reply names each role ID that the guild does not have. |
| `prompt guild` | A form with the text of the guild. An empty form removes the text. |
| `prompt role role:@R` | A form with the text of a role. An empty form removes the text. |
| `access add role:@R`, `access remove role:@R` | Adds or removes a role in `Access.QueryRoleIds`. |
| `limit role role:@R [tokens]` | Sets the limit of a role. No value removes it. |
| `limit user user:@U [tokens]` | Sets the limit of a member. No value removes it. |

Each change writes an audit line to the log. When two changes of a guild come at the same time, Optio applies each of them.

## Producers

A domain service sends events to Optio through NATS JetStream. Each event holds the text for the user and the policy that tells who can read it. The contract for such a producer is [docs/producers.md](docs/producers.md), with the schema [schemas/event.schema.json](schemas/event.schema.json). A new producer needs a NATS user, and no change in Optio.

The NATS configuration of development is [deploy/nats/nats.conf](../deploy/nats/nats.conf). It holds the users `custos` and `optio` with their permissions. Each user reads its password from an environment variable of the NATS server.

## Configuration

The section `Optio` in `appsettings.json` or environment variables (`Optio__PublicBaseUrl`). A secret can be a file in `/run/secrets`, for example `/run/secrets/Optio__OptioClientSecret`.

| Key | Default | Content |
|---|---|---|
| `PublicBaseUrl` | (required) | The origin of Optio as Custos sees it. |
| `Authority` | (required) | The issuer URL of the authorization server. |
| `OpenIdConfiguration` | `{Authority}/.well-known/openid-configuration` | The metadata document of the authorization server. |
| `CustosClientId` | `{PublicBaseUrl}/.well-known/custos-client` | The client ID of Custos. |
| `OptioClientId` | `optio` | The client ID of Optio, and the audience of a token of Custos. |
| `OptioClientSecret` | (required) | The secret of the client `optio`. |
| `UploadApiUrl` | (required) | The upload API of the server, for example `http://server:8080/api/v1/uploads`. |
| `UploadScope` | `custos:write` | The scope of the token for the upload API. |
| `ServiceName` | `Optio` | The name that Custos shows. |
| `ServiceDocumentation` | (none) | A page for humans about the service. |
| `Scopes` | `["openid", "custos:client"]` | The scopes that Custos requests. |
| `UploadMaxBytes` | `4194304` | The largest upload body. It must be equal to the value of the server. |
| `UploadMinIntervalSeconds` | `60` | The upload interval in the discovery document. It must be equal to the value of the server. |
| `ReplayWindowMinutes` | `15` | How long the broker keeps the events for a replay: the maximum age of the broker stream. |
| `StreamMaxMegabytes` | `100` | The maximum size of the broker stream. The broker then removes the oldest events. |
| `MaxStreamsPerUser` | `5` | The open streams of one user. |
| `DiscordToken` | (none) | The token of the Discord bot of Optio. Without it, Optio has no stream. |
| `DevelopmentGuildId` | (none) | The guild of a stream whose token has no claim `active_guild`. Only in the environment `Development`, as in the server. `1236389163276959888` in development. |
| `DevelopmentDiscordUserId` | (none) | The user of a stream whose token has no claim `discord_id`. Only in the environment `Development`. |
| `ClaudeApiKey` | (none) | The key of the Claude API. Keep it in `/run/secrets/Optio__ClaudeApiKey`. Without it, Optio answers no query. |
| `ModelId` | `claude-haiku-5-5` | The model of the query. |
| `ModelEffort` | `medium` | The effort of the model: `low`, `medium`, `high`, `xhigh`, or `max`. |
| `SystemPrompt` | (none) | The system prompt. [docs/system-prompt.example.txt](docs/system-prompt.example.txt) is an example. Without it, Optio answers no query. |
| `OperatorText` | (none) | The text about the services together: the words that they share, and how their records relate. [docs/operator-text.example.txt](docs/operator-text.example.txt) is an example. |
| `McpServices` | `[]` | The MCP services of the query. Each item has `Name` (lowercase letters and digits, at most 16), `Url`, `ReadScope`, and optionally `WriteScope`, `Title`, and `Card`. Without `WriteScope`, the query uses no write tool of the service. `Card` replaces the card that the service gives. |
| `HiddenTools` | `[]` | The tools that the model does not get, as `{service}_{tool}`. |
| `MaxQueriesPerHour` | `20` | The queries of one user in one hour. |
| `FollowUpMinutes` | `10` | The follow-up window, and the lifetime of a conversation. |
| `MaxConversationBytes` | `100000` | The largest conversation. Optio removes its oldest queries to fit. |
| `QuestionSeconds` | `60` | The lifetime of a question before a change. |
| `PartBudgetSeconds` | `60` | The budget of time of one part of a query. |
| `MaxToolCallsPerPart` | `8` | The budget of tool calls of one part of a query. |
| `CatalogMinutes` | `10` | How long Optio keeps the card and the tool list of a service. |
| `MaxSettingsFileBytes` | `2097152` (2 MiB) | The largest file of `/optioadmin settings update`, from 1024 bytes to 10 MiB. |
| `UsageRetentionDays` | `90` | How long Optio keeps the usage rows, from 1 to 3650 days. |

The connection string of the database is `ConnectionStrings:optio` (`ConnectionStrings__optio`), for PostgreSQL. Optio applies its migrations when it starts. Without it, Optio has no settings of a guild, no usage rows, and no `/optioadmin`, and the query still works.

The connection string of the broker is `ConnectionStrings:events` (`ConnectionStrings__events`), a NATS URL with the user `optio`, for example `nats://optio:...@broker:4222`. Without it, Optio has no stream. Optio starts without the broker, and connects again by itself.

## Health and telemetry

| Endpoint | Content |
|---|---|
| `GET /health/alive` | `Healthy` while the process runs. |
| `GET /health/ready` | `Healthy` when Optio can serve requests. With a database, Optio also checks it. |

- `optio --version` prints the version.
- `optio --healthcheck` asks `/health/alive` on this machine. The exit code is 0 for a healthy service. The `HEALTHCHECK` of the image uses it.
- Optio exports traces, metrics, and logs with OTLP when the environment has `OTEL_EXPORTER_OTLP_ENDPOINT`. Its activity source and its meter have the name `Optio`. The service name is `Optio`, unless `OTEL_SERVICE_NAME` sets another.

| Metric | Content |
|---|---|
| `optio.events` | The events that the reader of each open stream got, by `outcome`: `sent`, `denied` (the policy does not permit it), `no_bot` (the bot is not in the guild), or `invalid` (not valid, or an unknown policy version). |
| `optio.events.age` | The age of each event when Optio sends it, by `source`. A high age in normal operation shows that a client or Optio is slow. |
| `optio.streams` | The open streams. |
| `optio.queries` | The queries that closed, by `cause` of [spec/06-query.md](../spec/06-query.md), for example `answer`, `failed`, or `interrupted`. |
| `optio.tool.calls` | The tool calls of the queries, by `service`, `tool`, and `outcome`: `ok`, `error` (the tool gave an error), or `failed` (no result from the service). |
| `optio.model.tokens` | The tokens of the requests to the model, by `guild` and `type`: `input`, `output`, `cache_read`, or `cache_write`. |
| `optio.usage.failures` | The requests to the model whose token use did not go into the database. |

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

## Evaluation

The evaluation set examines whether the model selects the correct service and tool for a question. Run it when a card, the system prompt, the operator text, the tool list of a service, or the model changes. Each case is one call of the real model. CI does not run it, because each run costs money.

```sh
export Optio__ClaudeApiKey=...                  # required, else the tests skip
export Optio__EvaluationSet=/path/to/set.json   # optional: a set of the operator
dotnet test Optio.Tests --filter TestCategory=Evaluation
```

The repository holds the set of the server: [evaluation/custos.json](evaluation/custos.json). A test of the server makes sure that its card and its tools are equal to those of the server. A set with other services stays with the operator.

The model gets the system prompt and the tools as in a query. The system prompt is the example [docs/system-prompt.example.txt](docs/system-prompt.example.txt), or `Optio__SystemPrompt`. The other options also come from the environment, for example `Optio__ModelId` and `Optio__ModelEffort`.

| Member | Content |
|---|---|
| `services` | Each service: `name` and `title` as in the configuration, `card`, `write` (true when the configuration gives a `WriteScope`), and `tools` as the service gives them, each with `name`, `title`, `description`, `read_only`, and `input_schema`. |
| `hidden_tools` | As `HiddenTools` in the configuration. |
| `operator_text` | As `OperatorText` in the configuration. Optional. |
| `cases` | Each case: `question`, and `tools`, the tools that the model can call first, as `{service}_{tool}`. A case passes when the first turn of the model calls one tool or more, and only tools of the list. With an empty list, the model must answer with no tool call. |

## Container

The image is `ghcr.io/atlashq-gg/custos-optio`. The container serves HTTP on port 8080.

| Tag | Source |
|---|---|
| `X.Y.Z`, `X.Y` | The Git tag `optio-X.Y.Z`. |
| `X.Y.Z-<pre>` | The Git tag `optio-X.Y.Z-<pre>` of a pre-release, for example `optio-0.1.0-alpha.1`. A pre-release does not change `X.Y`. |
| `unstable` | Each push to `main` that changes `optio/`. |
| `sha-<commit>` | Each push to `main` that changes `optio/`. |

```sh
docker build -t custos-optio .
docker run --rm -p 8080:8080 custos-optio
```

## Deploy

[deploy/README.md](../deploy/README.md) deploys the full system: Keycloak, PostgreSQL, the broker, the Server, and Optio.

## License

AGPL-3.0-or-later ([LICENSE](LICENSE)).
