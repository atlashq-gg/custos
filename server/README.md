# Custos reference server

Contract version `v0`. License: AGPL-3.0-or-later ([LICENSE](LICENSE)), except the catalog of the game, which is under CC BY-NC-SA 4.0 ([catalog/LICENSE.md](catalog/LICENSE.md)).

The domain service of the guild records, behind [optio](../optio/README.md) ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). Custos calls Optio, and Optio relays the upload to the upload API of this server with a token of the user. The server parses the map data file and keeps the parsed data in PostgreSQL, for each Discord guild. It does not keep the file.

The server also runs as a Discord bot. The roles of a member in Discord give the access to the records of a guild. An MCP endpoint lets a user search the records through an LLM harness.

## Run

Requirements: .NET SDK 10 and Docker. The AppHost of Optio starts the full system: Keycloak, PostgreSQL, the RabbitMQ broker, this server on `http://localhost:5080`, and Optio ([optio/README.md](../optio/README.md#run)).

```sh
dotnet run --project ../optio/Optio.AppHost
```

The AppHost has two parameters, `discord-token` and `discord-user-id`. The dashboard asks for a value that is not set, and the user secrets can hold the values:

```sh
dotnet user-secrets set Parameters:discord-token <bot token> --project ../optio/Optio.AppHost
dotnet user-secrets set Parameters:discord-user-id <your Discord user ID> --project ../optio/Optio.AppHost
```

The server needs a bot token in the correct format: it stops at the start with a token that is not valid. With the bot token, the server connects to Discord, and the MCP endpoint is at `http://localhost:5080/mcp`. The Discord user ID is the user of a token without the claim `discord_id`. The realm gives the user `dev` no Discord user ID.

Keycloak imports [the realm](keycloak/custos-realm.json) on each start. Changes in the Keycloak console last for one run. The realm declares the user attribute `discord_id`, and its default client scopes `basic` and `custos-discord` put the claims `sub` and `discord_id` into each access token. A realm file that lists client scopes replaces all built-in scopes of Keycloak, so the file holds `basic` too.

The realm also holds the confidential client `optio` with the standard token exchange, and the optional scope `custos:client`. That scope adds the audience `optio` to the token of Custos, so that Optio can exchange the token for a token of this server. The AppHost makes a new secret for Optio on each start.

## Endpoints

| Endpoint | Content |
|---|---|
| `POST /api/v1/uploads` | The upload API. Optio relays the upload of Custos ([spec](../spec/03-upload.md)) to it. It parses the file, keeps the data for the guild of the user, and returns the stockpiles in the file. It needs no public route. |
| `GET /openapi/v1.json` | The OpenAPI document, generated from the code. |
| `GET /docs` | The Scalar page for the OpenAPI document. |
| `GET /health/alive` | The liveness check. It does not check the database. |
| `GET /health/ready` | The readiness check, with the database. |
| `POST /mcp` | The MCP endpoint ([MCP endpoint](#mcp-endpoint)). Only with `Mcp:Enabled` and the Discord connection. |
| `GET /.well-known/oauth-protected-resource` | The metadata document of the MCP endpoint (RFC 9728). |

## Configuration

The section `Custos` in `appsettings.json` or environment variables (`Custos__Authority`). The connection string of PostgreSQL is `ConnectionStrings:custos` (`ConnectionStrings__custos`). The connection string of the event broker is `ConnectionStrings:events`, an AMQP URI, for example `amqp://custos:...@broker:5672` ([Events](#events)). The AppHost sets both.

| Key | Default | Content |
|---|---|---|
| `Authority` | (required) | The issuer URL of the authorization server. |
| `Resource` | (required) | The audience of the tokens of the server: the URL of the MCP endpoint as a client sees it, for example `https://custos.example.com/mcp`. The upload API and `/mcp` accept a token only with this value in `aud`. `http://localhost:5080/mcp` in development. |
| `UploadMaxBytes` | `4194304` | The largest upload body. It must be equal to the value of Optio. |
| `UploadMinIntervalSeconds` | `60` | The shortest time between two uploads of one user. `10` in development. |
| `DevelopmentGuildId` | (none) | The guild of an upload whose token has no claim `active_guild`. The server uses it only in the environment `Development`. `1236389163276959888` in development. |
| `DiscordIdClaim` | `discord_id` | The claim of the access token with the Discord user ID, as a string. |
| `DevelopmentDiscordUserId` | (none) | The Discord user of an upload whose token has no claim `DiscordIdClaim`. The server uses it only in the environment `Development`. |
| `WarApiBaseUrl` | `https://war-service-live.foxholeservices.com` | The origin of the Foxhole war API. Each shard has its own host. The server gets the current war from `/api/worldconquest/war` on it. If the API does not answer, the server uses the last answer. Without an earlier answer, an upload gets `503`. |
| `Shard` | `live` | A stable label for the shard of `WarApiBaseUrl`. The war API does not label its shard. The label is a part of the ID of each structure, so do not change it during a war. The server supports one shard. |
| `CatalogPath` | `catalog/catalog.json` | The catalog of the game ([catalog/](catalog/README.md)). A relative path starts at the folder of the server. Without the file, each name is the code name. |
| `WarPollIntervalSeconds` | `300` | The time between two requests for the current war. `0` stops the requests. |

## Guilds

The server is multi-tenant. A tenant is a Discord guild. The access token names the guild of the user in the claim `active_guild`, as the guild ID in a string. A custom authenticator in Keycloak lets the user choose the guild and sets the claim. That authenticator is not part of this repository.

An upload without a valid `active_guild` gets `403`. In the environment `Development`, `DevelopmentGuildId` supplies the guild when the claim is absent. For production, see [The claim `active_guild`](../docs/deploy.md#the-claim-active_guild).

### Access for uploads

With the [Discord connection](#discord) on, an upload needs a member of the guild with the access `report` ([Guild settings](#guild-settings)). The access token names the Discord user in the claim `discord_id` ([The claim `discord_id`](../docs/deploy.md#the-claim-discord_id)).

| Case | Answer |
|---|---|
| The token has no valid claim `discord_id`. | `403` |
| The bot is not in the guild. | `403` |
| The user is not a member of the guild, or has no role that can report. | `403` |
| Discord does not answer. | `503`. Custos tries again at the next interval. |

In the environment `Development`, `DevelopmentDiscordUserId` supplies the user when the claim is absent. In the AppHost, set it with `dotnet user-secrets set Parameters:discord-user-id <id> --project ../optio/Optio.AppHost`.

The table `uploads` keeps the Discord user ID of each upload. It is null for an upload that came before the server had the Discord connection. Without the Discord connection, the server examines no role, and keeps the claim when the token has it.

## Discord

The server connects to Discord as a bot when the configuration has `Discord:Token` (`Discord__Token`). The token comes from the environment variable `Discord__Token`, or from the file `/run/secrets/Discord__Token`. A file in `/run/secrets` replaces an environment variable of the same key, also when the file is empty. Without the token, the connection is off, and the server writes a warning to the log when it starts. An upload then works as in 0.1.0.

- The bot uses the gateway with the intent `Guilds` only. It needs no privileged intent.
- The bot reads the roles of a member with Get Guild Member for each upload and each MCP call. A slash command gets the roles in the interaction.
- In development, set the token in the user secrets of the AppHost ([Run](#run)).

### The Discord application

1. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application.
2. On the page **Bot**, reset the token, and copy it. Keep the three privileged intents off.
3. Give the token to the server as `Discord:Token` ([Deploy](../docs/deploy.md#5-write-the-compose-file)).
4. Invite the bot to each guild with this link. Replace `<application ID>` with the ID on the page **General Information**:

   ```text
   https://discord.com/oauth2/authorize?client_id=<application ID>&scope=bot%20applications.commands&permissions=0
   ```

The bot sends no message to a channel, so the link asks for no permission. The server registers the commands `/custosadmin` and `/custos` when it starts.

NOTE: The Discord connection was tested on 2026-11-20 in one guild where the bot has a role with the Administrator permission: the commands, the access rule for uploads, and the MCP tools. A guild where the bot has no permission was not tested.

### Guild settings

A guild administrator configures the guild with `/custosadmin settings`. The two commands need the Administrator permission. Each reply is ephemeral.

| Command | Content |
|---|---|
| `/custosadmin settings view` | Shows the settings document, and attaches it as a file. |
| `/custosadmin settings update` | Replaces the full document. With the option `file`, from a JSON file. Without it, from a form that shows the current document. |

The document:

```json
{
  "SchemaVersion": 1,
  "Access": {
    "ReportRoleIds": [],
    "ReadRoleIds": ["111111111111111111"],
    "LabelRoleIds": ["222222222222222222"]
  },
  "Sharing": {
    "ReadGuildIds": ["333333333333333333"]
  }
}
```

| Member | Content |
|---|---|
| `Access.ReportRoleIds` | The roles that can upload. Empty: each member of the guild. |
| `Access.ReadRoleIds` | The roles that can read the records of the guild. Empty: an administrator only. |
| `Access.LabelRoleIds` | The roles that can change the label of a structure. Empty: an administrator only. |
| `Sharing.ReadGuildIds` | The other guilds that can read the records of this guild. |

- An administrator is the guild owner, or a member with the Administrator permission. An administrator has each kind of access.
- Each ID is a string. The server refuses a document with a `SchemaVersion` other than `1`, an unknown member, an ID that is not a snowflake, this guild in `ReadGuildIds`, or more than 25 entries in one list.
- The server accepts a role that the guild does not have, and names it in the reply.
- A guild without settings has the document with empty lists.
- The server writes one audit line to the log for each change, with the guild and the user.
- The server reads the roles of a member from Discord for each request, and keeps no cache. Thus a role change applies to the next request.

## Structure labels

The map data holds no name for a structure. The server generates a label for each new structure, and a user can change it.

- A world structure gets the place and the type, for example "Fort Barley Seaport". A structure is a world structure when the war API lists a map item at its coordinates.
- A player-built structure gets the place, the type, and a code, for example "Fort Barley Bunker Base K7Q2". The code is 4 characters of the structure ID in Crockford base32. Thus each guild generates the same label for one structure.
- The place is the nearest place name of the region, from the static map data of the war API. Without the map data of the region, the region name replaces the place.
- The type is the display name of the [catalog](catalog/README.md), without the tier. Without a catalog entry, the type is the code name.
- The label that shows is the label of a user, or else the generated label. It is unique in one guild and one war, without regard to case. If a generated label exists, the server adds the code, or more characters of the code.
- The server never changes a generated label. The structure ID includes the war, so the labels start again at each war.
- When it starts, the server generates a label for each structure that has none.

`/custos label` changes a label. The option `structure` has autocomplete on the labels of the current war. Without the option `label`, the generated label shows again. The command needs the access `label` ([Guild settings](#guild-settings)).

A user label has 1 to 64 characters: letters, digits, spaces, and `-` `'` `.` `,` `(` `)` `/` `&` `+`. A label goes into Discord messages and into the context of an LLM, so the server refuses other characters. A change of a label is not a change of the structure: the history does not record it. The server writes one audit line to the log for each change.

## MCP endpoint

`/mcp` is an MCP (Model Context Protocol) endpoint with stateless HTTP. A user searches the records of a guild through an LLM harness. The endpoint is an OAuth resource server: the authorization server issues the tokens, and the server validates them. It needs `Mcp:Enabled` and the [Discord connection](#discord).

| Key | Default | Content |
|---|---|---|
| `Mcp:Enabled` | `false` | Without `true`, the server maps no MCP endpoint. |
| `Mcp:Authority` | `Custos:Authority` | The issuer of the MCP tokens. |
| `Mcp:ReadScope` | `custos:read` | The scope of the read tools. |
| `Mcp:WriteScope` | `custos:write` | The scope of the label tool and of the upload API. |
| `Mcp:RequestsPerMinute` | `60` | The largest number of requests of one caller in one minute. Each call can cost a request to Discord. |

- `Custos:Resource` is the URL of the endpoint as a harness sees it. Its path must be `/mcp`.
- The server serves the metadata document (RFC 9728) at `/.well-known/oauth-protected-resource` on the origin of `Custos:Resource`. `/.well-known/oauth-protected-resource/mcp` redirects to it.
- A request without a valid token gets `401` with `WWW-Authenticate: Bearer resource_metadata="...", scope="custos:read custos:write"`.
- The token needs the audience `Custos:Resource`, the claim `discord_id`, and the scope of the tool. A token of Optio from a token exchange has the same form.
- The upload API accepts a token with the audience `Custos:Resource` and the scope `custos:write`. The Discord roles of the member decide the rest. Thus an MCP harness with `custos:write` can also call the upload API.
- A tool without the scope of the token is not in the list of tools. A call without the scope gets `403` with `error="insufficient_scope"`.
- A refusal for access is a tool result with `isError` and a problem body: `urn:custos:mcp:forbidden`, `urn:custos:mcp:not-visible`, or `urn:custos:mcp:discord-unavailable`. A missing guild, a guild without the bot, and a guild where the caller is not a member get the same `not-visible` answer.
- The server writes one audit line to the log for each tool call.
- The SDK serves the metadata document only to a request whose scheme and host are equal to `Custos:Resource`. Behind a reverse proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so that the server applies `X-Forwarded-Proto` and `X-Forwarded-For`.

| Tool | Scope | Access | Result |
|---|---|---|---|
| `list_guilds` | read | (token only) | Each guild of the bot where the caller is a member: the ID, the name, the access, the current war, and the guilds that share their records with it. |
| `find_items` | read | `read` | One row for each matching item in each stockpile: the item, the quantity, the stockpile, the structure with its label, and the time of the last sighting. One total for each item. |
| `find_structures` | read | `read` | One row for each matching structure: the label, the generated label, the type, the category, the region, the place, and its stockpiles with their entries. |
| `set_structure_label` | write | `label` | Changes the label of a structure in the records of the guild, or removes it. The result gives the old label and the new label. |

The search tools take the argument `guild_id`, and the filters `items`, `stockpiles`, `structures`, and `structure_categories`.

- Each filter is a list. A row matches when it matches one value of each list that the call gives. A filter that matches nothing gives an empty list, not a refusal.
- `items` matches the code name or the display name of the [catalog](catalog/README.md), without regard to case. A code name without a catalog entry matches too.
- `stockpiles` takes a stockpile ID, a part of a name, or `public`. `structures` takes a structure ID or a part of a label. `structure_categories` matches the category of the catalog or the code name.
- `war` is a war number. The default is the current war.
- `limit` is 50 by default and 200 at most. `total` gives the number of all rows, and `next_cursor` gets the next page.
- `applied` gives the war, the access of the caller, and the code names that each item filter matched.
- A search for guild B reads the records of B and of each guild that lists B in `Sharing.ReadGuildIds`. A row from another guild names it in `origin_guild`. For a stockpile that two guilds know, the row comes from the record with the latest sighting. A structure shows the label of B when B has its own record of it, else the label of the guild of the record.
- `set_structure_label` changes only a record of the guild of `guild_id`. It applies the rules of a user label ([Structure labels](#structure-labels)). Its audit line names MCP as the origin. A token without the write scope does not list it.
- An item shows `crate_size` and `pallet_size` when the [catalog](catalog/README.md) has them. A row or an entry with `crated` true counts crates, so the number of units is `quantity` times `crate_size`. `crate_size` is the number in an item crate, or in a shippable crate for a vehicle or a structure. `pallet_size` is the number of items in a pallet.
- The names of reserve stockpiles and the labels are text from players. The results give them as values of JSON members only.

The development realm has the optional client scopes `custos:read` and `custos:write`, each with an audience mapper for `CUSTOS_MCP_RESOURCE` and the `discord_id` mapper. It permits an anonymous client registration from `localhost` and `127.0.0.1`, and each client must use PKCE with S256. The user `dev` has the realm role `offline_access`, because Claude Code requests that scope. It also accepts the Client ID Metadata Document of Claude Code (`https://claude.ai/oauth/claude-code-client-metadata`): the profile `custos-cimd` permits the domains `claude.ai`, `localhost`, and `127.0.0.1`, and the resource of the MCP endpoint. Keycloak starts with the features `cimd` and `resource-indicators`.

## Events

When an upload changes a stockpile, the server publishes an event to the event broker. The contract is [optio/docs/producers.md](../optio/docs/producers.md), and the decision is [ADR 0007](../docs/adr/0007-a-broker-carries-domain-events.md).

- Without `ConnectionStrings:events`, the server publishes no events.
- The server publishes after the commit of the upload, to the exchange `events.custos`, with `mandatory` true. The routing key is `<guild>.stockpile.changed`.
- The server publishes one event for each changed stockpile and each guild that can read it. These are the guild of the upload and each guild in its `Sharing.ReadGuildIds`. The policy of an event names that guild and its `Access.ReadRoleIds`.
- An upload does not wait for the broker, and it succeeds without the broker. The server keeps at most 1000 events in memory while the broker is not available, and drops the oldest. It does not publish an event again.
- The metric `custos.events` counts the events by outcome: `published`, `returned` (the stream or its binding is missing), `dropped` (the queue was full), and `failed`.
- The broker user of the server can only write to `events.custos`.

An event of the type `stockpile.changed` has these members in `data`, after `policy`:

| Member | Content |
|---|---|
| `message.text` | The text for a person. The first line holds the label and the stockpile. Then come at most 5 changes, each with its signed difference and the new total in parentheses, for example `Rifle crates +20 (40)` or `Basic Materials -400 (0)`. Then comes `+N more`. A new stockpile gives one line, for example `Fort Barley Seaport, Main: new, 12 items`. At most 500 characters. |
| `message.reference` | `stockpile:<stockpile ID>`, so that a new change of the stockpile replaces its message in Custos. |
| `guild_id` | The guild of the policy. |
| `origin_guild` | The guild of the upload. |
| `stockpile_id`, `stockpile_name` | The stockpile. The public stockpile has the name `Public`. |
| `structure_id`, `structure_label` | The structure of the stockpile. When the guild of the policy has its own record, the label is its label, as in a search. |
| `war` | The war number. |
| `observed_at` | The time of the change in the game, from the file. |

The `time` of the event is the time when the server received the upload. The text gives the most important change first. An entry that goes to 0 comes first, then the largest change in crates. A decrease comes before an increase of the same size. A loose entry counts as its quantity divided by the crate size of the catalog. When only an entry with the quantity 0 changed, the server publishes no event.

## Storage

The server keeps the parsed data in PostgreSQL. It applies its migrations when it starts. Issue #1 holds the design ("Extension: guild storage in `server/`").

- `guild_id`, then `war_id`, start the key of each table of guild data. `wars` and `guild_settings` are the tables without `war_id`. `wars` is the only table without `guild_id`.
- `guild_settings` holds the settings document of each guild as `jsonb` ([Guild settings](#guild-settings)).
- `structures` has `generated_label` and `label` ([Structure labels](#structure-labels)). A unique index on `lower(coalesce(label, generated_label))` keeps the label that shows unique in one guild and one war.
- `map_regions`, `map_places`, and `map_items` hold the map data of each region for each war. They are reference data, as `wars` is.
- The current state is in `structures`, `stockpiles`, and `stockpile_entries`. The history is in `structure_changes` and `stockpile_changes`. The history keeps a change, not a sighting: for equal content, the server only moves `last_seen_at`. Data that is not newer than the last sighting changes nothing.
- The ID of a structure is a UUID version 5 of the shard, the war, the region, and the coordinates as exact float32. The ID of a stockpile is a UUID version 5 of the structure and the name. Thus each guild calculates the same IDs ([EntityIds.cs](Custos.Server/Storage/EntityIds.cs)).
- The server keeps a public stockpile only when it has entries, or when the server already knows it.
- The server ignores a pinned item with `LastUpdated` before the start of the war.

### Wars

The war monitor asks the war API for the current war on an interval. An upload does the same. For a new war, the server adds the row to `wars`, creates the partitions of the two history tables, and sets `ended_at` on the earlier war. Then the war monitor gets the static map data and the public dynamic map data of each region one time for the war. The request for the static data has `If-None-Match` with the `ETag` of the earlier war. A region that does not answer gets a new request at the next interval. A partition name has the war number, for example `stockpile_changes_w142`. The archive and the purge of a war are not automated.

### Migrations

The server applies all pending migrations when it starts, before it accepts requests. The container image does this too, so a deployment needs no .NET SDK and no separate migration step. EF Core locks the migration history while it applies migrations.

To add a migration in development:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project Custos.Server --output-dir Storage/Migrations
```

EF Core cannot declare a partitioned table. A migration that creates or changes a history table must use SQL for it, as the migration `Initial` does.

## Authentication with Keycloak

Custos logs in through Optio: Keycloak gets the Client ID Metadata Document (CIMD) of Custos from Optio ([optio/README.md](../optio/README.md)). The server sees only tokens that Optio gets through a token exchange (RFC 8693), and tokens of MCP harnesses.

- A token of the server has the audience `Custos:Resource`. The scope of the server adds it: `custos:read` or `custos:write`.
- An exchanged token has `azp` `optio`, the claim `discord_id`, and the claims of the scopes of the client `optio`. A claim of a scope that only the client of Custos has, for example `active_guild` from a hardcoded scope, is not in it. Give such a scope to the client `optio` too.
- An exchanged token expires no later than the token of Custos.

## Telemetry and health

The server sends traces, metrics, and logs through OpenTelemetry. It exports them with OTLP only when the environment has `OTEL_EXPORTER_OTLP_ENDPOINT`. The standard variables `OTEL_EXPORTER_OTLP_*` configure the exporter. The service name is `Custos`, and `OTEL_SERVICE_NAME` replaces it. The server also adds `service.version`. In development, the Aspire dashboard shows the three signals.

- Instrumentation: ASP.NET Core, `HttpClient`, the .NET runtime, Npgsql, and the metrics of EF Core. The traces leave out the health endpoints.
- The server has one activity source and one meter, each with the name `Custos.Server` and the version of the build.

| Instrument | Unit | Content |
|---|---|---|
| `custos.uploads` | `{upload}` | The uploads that reached the handler, with the tag `outcome`: `accepted`, `forbidden`, `too_large`, `not_map_data`, `unavailable`, or `error`. A refusal before the handler (`401`, `415`, `429`) is only in the metrics of ASP.NET Core. |
| `custos.upload.parse.duration` | `s` | The time to parse the map data file. |
| `custos.discord.request.duration` | `s` | The requests of the bot to the REST API of Discord, with the tag `outcome`: `ok`, `client_error`, `rate_limited` (`429`), `server_error` (`5xx`), or `error` (no answer). |
| `custos.war_api.answers` | `{answer}` | The requests to the war API, with the tag `outcome`: `fresh`, `stale` (the server used the last answer), or `none`. |

The health checks have tags. `/health/alive` runs the checks with the tag `live`, and `/health/ready` the checks with the tag `ready`. `ready` holds the database only.

Two flags run before the server starts:

| Flag | Content |
|---|---|
| `--version` | Writes the version of the build, and stops. |
| `--healthcheck` | Gets `/health/alive` of the server on this machine, and stops. The exit code is `0` for a healthy server, and `1` for all other answers. The URL comes from `ASPNETCORE_URLS`, else from `ASPNETCORE_HTTP_PORTS`, else port 8080. |

The client of the war API has the standard resilience handler: 3 attempts of at most 5 seconds each, and 15 seconds in total. After that, the rule of the last answer applies (`WarApiBaseUrl` in [Configuration](#configuration)).

## Format

[CSharpier](https://csharpier.com) formats the C# code. `.editorconfig` sets the line width to 100 characters.

```sh
dotnet tool restore
dotnet csharpier format .
```

CSharpier does not wrap comments and strings. Keep them at 100 characters or less by hand. CI runs `dotnet csharpier check .` and fails on a longer line. The generated migration files are excluded.

## Tests

```sh
dotnet test
```

The tests check the JSON documents against the schemas in [spec/schemas](../spec/schemas/). The tests need Docker: they start PostgreSQL with Testcontainers, and each test server gets its own database. The test in the category `Integration` also starts Keycloak and does the full login. Use `--filter "Category!=Integration"` to skip it.

## Container

CI publishes the image `ghcr.io/atlashq-gg/custos-server`:

| Tag | Content |
|---|---|
| `X.Y.Z`, `X.Y` | The release from the Git tag `server-X.Y.Z`. |
| `unstable` | The latest build of `main`. |
| `sha-<commit>` | The build of one commit on `main`. |

```sh
docker pull ghcr.io/atlashq-gg/custos-server:unstable
```

The image holds the catalog of the game, a file from the Foxhole wiki under CC BY-NC-SA 4.0 ([catalog/LICENSE.md](catalog/LICENSE.md)). The label `org.opencontainers.image.licenses` names both licenses. The NonCommercial term applies to the image. For a commercial use, build the image without `catalog/catalog.json`.

To build and run the image locally:

```sh
docker build -t custos-server .
docker run -p 8080:8080 -e Custos__PublicBaseUrl=https://custos.example.com -e Custos__Authority=https://id.example.com/realms/custos -e ConnectionStrings__custos="Host=db;Database=custos;Username=custos;Password=..." custos-server
```

## Deploy

[docs/deploy.md](../docs/deploy.md) deploys the full system: Keycloak, PostgreSQL, the broker, this server, and Optio. Only `/mcp` and its metadata document need a public route.
