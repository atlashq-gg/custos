# Custos reference server

Version 0.2.0. Contract version `v0`. License: AGPL-3.0-or-later ([LICENSE](LICENSE)), except the catalog of the game, which is under CC BY-NC-SA 4.0 ([catalog/LICENSE.md](catalog/LICENSE.md)).

The reference implementation of the [Custos specification](../spec/README.md). It serves the discovery document and the Client ID Metadata Document of Custos, and it accepts uploads. It parses the map data file and keeps the parsed data in PostgreSQL, for each Discord guild. It does not keep the file.

The server also runs as a Discord bot. The roles of a member in Discord give the access to the records of a guild. An MCP endpoint lets a user search the records through an LLM harness.

## Run

Requirements: .NET SDK 10 and Docker.

```sh
dotnet run --project Custos.Server.AppHost
```

The AppHost starts:

| Resource | URL | Content |
|---|---|---|
| Server | `http://localhost:5080` | The service URL for Custos. |
| PostgreSQL 18 | (dashboard) | The database `custos`. The data lasts for one run. |
| Keycloak | `http://localhost:8180` | The realm `custos` with the user `dev`, password `dev`. Admin user `admin`. Its password is in the dashboard. |
| Dashboard | `http://localhost:15180` | The Aspire dashboard. The login URL is in the console output. It shows the traces, the metrics, and the logs of the server. |

The AppHost has two parameters, `discord-token` and `discord-user-id`. The dashboard asks for a value that is not set, and the user secrets can hold the values:

```sh
dotnet user-secrets set Parameters:discord-token <bot token> --project Custos.Server.AppHost
dotnet user-secrets set Parameters:discord-user-id <your Discord user ID> --project Custos.Server.AppHost
```

With the bot token, the server connects to Discord, and the MCP endpoint is at `http://localhost:5080/mcp`. The Discord user ID is the user of a token without the claim `discord_id`. The realm gives the user `dev` no Discord user ID.

Keycloak imports [the realm](Custos.Server.AppHost/keycloak/custos-realm.json) on each start. Changes in the Keycloak console last for one run. The realm declares the user attribute `discord_id`, and its default client scopes `basic` and `custos-discord` put the claims `sub` and `discord_id` into each access token. A realm file that lists client scopes replaces all built-in scopes of Keycloak, so the file holds `basic` too.

## Endpoints

| Endpoint | Content |
|---|---|
| `GET /.well-known/custos-configuration` | The discovery document ([spec](../spec/01-discovery.md)). |
| `GET /.well-known/custos-client` | The Client ID Metadata Document (CIMD) of Custos. Its URL is the client ID. |
| `POST /api/v1/uploads` | The upload ([spec](../spec/03-upload.md)). It parses the file, keeps the data for the guild of the user, and returns the stockpiles in the file. |
| `GET /openapi/v1.json` | The OpenAPI document, generated from the code. |
| `GET /docs` | The Scalar page for the OpenAPI document. |
| `GET /health/alive` | The liveness check. It does not check the database. |
| `GET /health/ready` | The readiness check, with the database. |
| `POST /mcp` | The MCP endpoint ([MCP endpoint](#mcp-endpoint)). Only with `Mcp:Enabled` and the Discord connection. |
| `GET /.well-known/oauth-protected-resource` | The metadata document of the MCP endpoint (RFC 9728). |

## Configuration

The section `Custos` in `appsettings.json` or environment variables (`Custos__PublicBaseUrl`). The connection string of PostgreSQL is `ConnectionStrings:custos` (`ConnectionStrings__custos`). The AppHost sets it.

| Key | Default | Content |
|---|---|---|
| `PublicBaseUrl` | (required) | The origin of the server as Custos sees it. |
| `Authority` | (required) | The issuer URL of the authorization server. |
| `OpenIdConfiguration` | `{Authority}/.well-known/openid-configuration` | The metadata document of the authorization server, as the discovery document links it. |
| `ClientId` | `{PublicBaseUrl}/.well-known/custos-client` | The client ID of Custos. |
| `ServiceName` | `Custos reference server` | The name that Custos shows. |
| `Scopes` | `["openid"]` | The scopes that Custos requests. |
| `UploadMaxBytes` | `4194304` | The largest upload body. |
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

An upload without a valid `active_guild` gets `403`. In the environment `Development`, `DevelopmentGuildId` supplies the guild when the claim is absent. For production, see [The claim `active_guild`](#the-claim-active_guild).

### Access for uploads

With the [Discord connection](#discord) on, an upload needs a member of the guild with the access `report` ([Guild settings](#guild-settings)). The access token names the Discord user in the claim `discord_id` ([The claim `discord_id`](#the-claim-discord_id)).

| Case | Answer |
|---|---|
| The token has no valid claim `discord_id`. | `403` |
| The bot is not in the guild. | `403` |
| The user is not a member of the guild, or has no role that can report. | `403` |
| Discord does not answer. | `503`. Custos tries again at the next interval. |

In the environment `Development`, `DevelopmentDiscordUserId` supplies the user when the claim is absent. In the AppHost, set it with `dotnet user-secrets set Parameters:discord-user-id <id> --project Custos.Server.AppHost`.

The table `uploads` keeps the Discord user ID of each upload. It is null for the rows of 0.1.0. Without the Discord connection, the server examines no role, as in 0.1.0, and keeps the claim when the token has it.

## Discord

The server connects to Discord as a bot when the configuration has `Discord:Token` (`Discord__Token`). The token comes from the environment variable `Discord__Token`, or from the file `/run/secrets/Discord__Token`. A file in `/run/secrets` replaces an environment variable of the same key, also when the file is empty. Without the token, the connection is off, and the server writes a warning to the log when it starts. An upload then works as in 0.1.0.

- The bot uses the gateway with the intent `Guilds` only. It needs no privileged intent.
- The bot reads the roles of a member with Get Guild Member for each upload and each MCP call. A slash command gets the roles in the interaction.
- In development, set the token in the user secrets of the AppHost ([Run](#run)).

### The Discord application

1. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application.
2. On the page **Bot**, reset the token, and copy it. Keep the three privileged intents off.
3. Give the token to the server as `Discord:Token` ([Deploy](#3-write-the-compose-file)).
4. Invite the bot to each guild with this link. Replace `<application ID>` with the ID on the page **General Information**:

   ```text
   https://discord.com/oauth2/authorize?client_id=<application ID>&scope=bot%20applications.commands&permissions=0
   ```

The bot sends no message to a channel, so the link asks for no permission. The server registers the commands `/custosadmin` and `/custos` when it starts.

NOTE: Version 0.2.0 was tested on 2026-11-20 in one guild where the bot has a role with the Administrator permission: the commands, the access rule for uploads, and the MCP tools. A guild where the bot has no permission was not tested.

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
- The server generates the labels of the structures of 0.1.0 when it starts.

`/custos label` changes a label. The option `structure` has autocomplete on the labels of the current war. Without the option `label`, the generated label shows again. The command needs the access `label` ([Guild settings](#guild-settings)).

A user label has 1 to 64 characters: letters, digits, spaces, and `-` `'` `.` `,` `(` `)` `/` `&` `+`. A label goes into Discord messages and into the context of an LLM, so the server refuses other characters. A change of a label is not a change of the structure: the history does not record it. The server writes one audit line to the log for each change.

## MCP endpoint

`/mcp` is an MCP (Model Context Protocol) endpoint with stateless HTTP. A user searches the records of a guild through an LLM harness. The endpoint is an OAuth resource server: the authorization server issues the tokens, and the server validates them. It needs `Mcp:Enabled` and the [Discord connection](#discord).

| Key | Default | Content |
|---|---|---|
| `Mcp:Enabled` | `false` | Without `true`, the server maps no MCP endpoint. |
| `Mcp:Resource` | (required) | The URL of the endpoint as a harness sees it, for example `https://custos.example.com/mcp`. Its path is `/mcp`. A token must have it in `aud`. |
| `Mcp:Authority` | `Custos:Authority` | The issuer of the MCP tokens. |
| `Mcp:ReadScope` | `custos:read` | The scope of the read tools. |
| `Mcp:WriteScope` | `custos:write` | The scope of the label tool. |
| `Mcp:RequestsPerMinute` | `60` | The largest number of requests of one caller in one minute. Each call can cost a request to Discord. |

- The server serves the metadata document (RFC 9728) at `/.well-known/oauth-protected-resource` on the origin of `Mcp:Resource`. `/.well-known/oauth-protected-resource/mcp` redirects to it.
- A request without a valid token gets `401` with `WWW-Authenticate: Bearer resource_metadata="...", scope="custos:read custos:write"`.
- The token needs the audience `Mcp:Resource`, the claim `discord_id`, and the scope of the tool. The token of Custos has no audience, so `/mcp` refuses it. An MCP token has another `azp`, so the upload refuses it.
- A tool without the scope of the token is not in the list of tools. A call without the scope gets `403` with `error="insufficient_scope"`.
- A refusal for access is a tool result with `isError` and a problem body: `urn:custos:mcp:forbidden`, `urn:custos:mcp:not-visible`, or `urn:custos:mcp:discord-unavailable`. A missing guild, a guild without the bot, and a guild where the caller is not a member get the same `not-visible` answer.
- The server writes one audit line to the log for each tool call.
- The SDK serves the metadata document only to a request whose scheme and host are equal to `Mcp:Resource`. Behind a reverse proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so that the server applies `X-Forwarded-Proto` and `X-Forwarded-For`.

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

Keycloak 26.8 has the experimental feature `cimd`. The realm has a client policy that accepts a client ID that is a URL on the host of the server. Keycloak then gets the registration of Custos from the CIMD document.

- Keycloak must reach the server at the client ID URL. In development, Keycloak runs in a container and uses the Aspire container tunnel, so the client ID is `http://aspire.dev.internal:5080/.well-known/custos-client`.
- The policy permits the domain `oauth2callback`, because Keycloak reads the redirect URI `custos://oauth2callback` as a URL with that host.
- The development realm permits HTTP. A production realm must use HTTPS for the client ID ([Prepare Keycloak](#2-prepare-keycloak)).
- A token for Custos has no audience. The server requires the claim `azp` to be the client ID.
- Keycloak shows a consent page for the client on the first login.

## Telemetry and health

The server sends traces, metrics, and logs through OpenTelemetry. It exports them with OTLP only when the environment has `OTEL_EXPORTER_OTLP_ENDPOINT`. The standard variables `OTEL_EXPORTER_OTLP_*` configure the exporter. Set the name of the service with `OTEL_SERVICE_NAME`. The server adds only `service.version`. In development, the Aspire dashboard shows the three signals.

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
| `X.Y.Z`, `X.Y` | The release from the Git tag `server/vX.Y.Z`. |
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

This section deploys the container with Docker Compose against an existing PostgreSQL and an existing Keycloak. The examples use these values. Replace them with your own.

| Value | Example |
|---|---|
| Public URL of the server | `https://custos.example.com` |
| Issuer of the Keycloak realm | `https://id.example.com/realms/custos` |
| Database, role | `custos`, `custos` |

The server needs:

- PostgreSQL 18. The tests and the AppHost use 18 only. The server uses list partitions and advisory locks.
- Keycloak 26.8 or later, with the feature `cimd`, and `resource-indicators` for the MCP endpoint.
- A reverse proxy that terminates TLS. The container serves HTTP on port 8080.
- A Discord application with a bot, for the Discord connection and the MCP endpoint ([The Discord application](#the-discord-application)).

### 1. Prepare PostgreSQL

Create a role and a database that the role owns. The server applies its migrations when it starts, so the role must be able to create tables in the database.

```sql
CREATE ROLE custos LOGIN PASSWORD 'change-me';
CREATE DATABASE custos OWNER custos;
```

The role needs no other privilege. Make sure that the container can reach PostgreSQL, for example through a shared Docker network (step 3).

### 2. Prepare Keycloak

1. Start Keycloak with the feature `cimd`: the option `--features=cimd`, or the environment variable `KC_FEATURES=cimd`. For the MCP endpoint, add the feature `resource-indicators`: `--features=cimd,resource-indicators`. Keep other features that you already enable in the same list.
2. Select a realm for Custos, or create one. The issuer of the realm is the value of `Custos__Authority`.
3. Add the client profile and the client policy of Custos to the realm (see below).
4. Set the session lifetime of the realm. Custos does not request `offline_access`, so the SSO session sets how long a login of Custos lasts. The development realm uses 7 days idle (`ssoSessionIdleTimeout` 604800) and 30 days at most (`ssoSessionMaxLifespan` 2592000).
5. Add the claim `active_guild` (see below).
6. Add the claim `discord_id` (see below).
7. For the MCP endpoint, add the MCP scopes and the registration policies (see below).

#### Client profile and client policy

The profile lets Keycloak get the registration of Custos from the Client ID Metadata Document of the server. The policy applies the profile to a client ID on the host of the server. Put the host of the public URL in both files. The domain `oauth2callback` permits the redirect URI `custos://oauth2callback`.

`profiles.json`:

```json
{
  "profiles": [
    {
      "name": "custos-cimd",
      "description": "Accepts the metadata document of Custos.",
      "executors": [
        {
          "executor": "client-id-metadata-document",
          "configuration": {
            "cimd-allow-http-scheme": "false",
            "cimd-allow-permitted-domains": ["custos.example.com", "oauth2callback"]
          }
        }
      ]
    }
  ]
}
```

`policies.json`:

```json
{
  "policies": [
    {
      "name": "custos-cimd",
      "description": "Clients whose ID is the URL of the metadata document on the Custos server.",
      "enabled": true,
      "conditions": [
        {
          "condition": "client-id-uri",
          "configuration": {
            "client-id-uri-scheme": ["https"],
            "client-id-uri-allow-permitted-domains": ["custos.example.com"]
          }
        }
      ],
      "profiles": ["custos-cimd"]
    }
  ]
}
```

CAUTION: Each command replaces all client profiles or all client policies of the realm. If the realm already has profiles or policies, get them first and add the entries of Custos to the lists.

```sh
kcadm.sh config credentials --server https://id.example.com --realm master --user admin
kcadm.sh get realms/custos/client-policies/profiles
kcadm.sh get realms/custos/client-policies/policies
kcadm.sh update realms/custos/client-policies/profiles -f profiles.json
kcadm.sh update realms/custos/client-policies/policies -f policies.json
```

The admin console has the same settings in **Realm settings**, on the tab **Client policies**.

#### The claim `active_guild`

Outside the environment `Development`, the server ignores `DevelopmentGuildId`. An upload whose token has no claim `active_guild` gets `403`. The authenticator that lets a user choose a guild is not built yet ([#1](https://github.com/atlashq-gg/custos/issues/1)).

For one guild, a hardcoded claim gives each token of the realm the same guild:

1. In **Client scopes**, create the scope `custos-guild` with the protocol `OpenID Connect`. Set **Include in token scope** to off.
2. In the tab **Mappers** of the scope, configure a new mapper of the type **Hardcoded claim** with these values:
   - **Token Claim Name**: `active_guild`
   - **Claim value**: the guild ID
   - **Claim JSON Type**: `String`
   - **Add to access token**: on
3. In the list **Client scopes**, set the assigned type of `custos-guild` to **Default**. Keycloak then adds the scope to each new client, also to the client that it registers through CIMD.

If Keycloak registered the client of Custos before step 3, add the scope to that client in **Clients**, on the tab **Client scopes**.

This method works with Keycloak 26.8 and a client that Keycloak registered through CIMD (tested on 2026-11-18). Remove the scope when the authenticator sets the claim.

#### The claim `discord_id`

With the Discord connection on, an upload needs the Discord user ID in the claim `discord_id` of the access token. The development realm shows one method: a user attribute and a default client scope.

1. In **Realm settings**, on the tab **User profile**, create the attribute `discord_id`. Let only an administrator edit it. Keycloak 24 and later remove a user attribute that the user profile does not declare.
2. In **Client scopes**, create the scope `custos-discord` with the protocol `OpenID Connect`. Set **Include in token scope** to off.
3. In the tab **Mappers** of the scope, configure a new mapper of the type **User Attribute**: **User Attribute** `discord_id`, **Token Claim Name** `discord_id`, **Claim JSON Type** `String`, **Add to access token** on.
4. In the list **Client scopes**, set the assigned type of `custos-discord` to **Default**.
5. Set the attribute `discord_id` of each user to the Discord user ID.

The authenticator that sets `active_guild` can also set this claim. That work is outside this repository.

#### The MCP scopes and the registration of a harness

An LLM harness reads the metadata document of `/mcp`, gets its own client in the realm, and asks the user to log in. A harness gets a client in one of two ways. It sends a registration request, or it uses the URL of its Client ID Metadata Document as the client ID. Keycloak with the feature `cimd` states that it supports the second way, so a harness such as Claude Code uses it and sends no registration request. The token needs the audience of the endpoint, the claim `discord_id`, and a scope. The [development realm](Custos.Server.AppHost/keycloak/custos-realm.json) holds each of these settings.

1. In **Client scopes**, create the scopes `custos:read` and `custos:write`, each with the protocol `OpenID Connect` and **Include in token scope** on.
2. In the tab **Mappers** of each scope, add two mappers:
   - **Audience**: **Included Custom Audience** `https://custos.example.com/mcp`, **Add to access token** on.
   - **User Attribute**: as in [The claim `discord_id`](#the-claim-discord_id).
3. In the list **Client scopes**, set the assigned type of the two scopes to **Optional**. A default scope also goes to the client of Custos, and its tokens then get the audience of `/mcp`.
4. In **Clients**, on the tab **Client registration**, open the anonymous policy **Trusted Hosts**. Add the hosts of the redirect URIs of the harnesses that you permit, for example `localhost` and `127.0.0.1` for a harness on a computer. Set **Host Sending Client Registration Request Must Match** to off, and keep **Client URIs Must Match** on.
5. In **Realm settings**, on the tab **Client policies**, add a profile with the executor `pkce-enforcer` (**Auto-configure** on), and a policy with the condition `any-client` for that profile. Each client then uses PKCE with S256.
6. For a harness that uses a Client ID Metadata Document, such as Claude Code, extend the profile and the policy `custos-cimd` ([Client profile and client policy](#client-profile-and-client-policy)). Without this step, Keycloak shows `Client not found`, because the policy accepts only the host of the server. In the profile, add to the executor `client-id-metadata-document`:

   ```json
   "cimd-allow-permitted-domains": ["custos.example.com", "oauth2callback", "claude.ai", "localhost", "127.0.0.1"],
   "cimd-resource-indicator-allow-list": ["https://custos.example.com/mcp"]
   ```

   `claude.ai` is the host of the client ID. `localhost` and `127.0.0.1` are the hosts of the redirect URIs. Without the allow list of resources, Keycloak refuses the parameter `resource` with `invalid_target`. In the policy, add `claude.ai` to `client-id-uri-allow-permitted-domains` of the condition. For another harness, add the host of its metadata document, and the hosts of its redirect URIs.
7. Claude Code requests the scope `offline_access`. A user needs the realm role `offline_access`. Without it, Keycloak answers the token request with `Offline tokens not allowed for the user or client`. A new realm gives this role to each user in its default roles, so this step is for a realm that removed it.

To add the endpoint to a harness, give it the URL of the endpoint as a remote MCP server with the HTTP transport. For example, in Claude Code:

```sh
claude mcp add --transport http custos https://custos.example.com/mcp
```

The harness opens the login of Keycloak. The consent page shows the two scopes. A token with `custos:read` only does not list the label tool.

### 3. Write the Compose file

```yaml
services:
  custos-server:
    image: ghcr.io/atlashq-gg/custos-server:0.2.0
    restart: unless-stopped
    environment:
      Custos__PublicBaseUrl: https://custos.example.com
      Custos__Authority: https://id.example.com/realms/custos
      ConnectionStrings__custos: >-
        Host=postgres;Port=5432;Database=custos;Username=custos;Password=${CUSTOS_DB_PASSWORD}
      Mcp__Enabled: "true"
      Mcp__Resource: https://custos.example.com/mcp
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      OTEL_SERVICE_NAME: custos-server
      # OTEL_EXPORTER_OTLP_ENDPOINT: http://collector:4317
    secrets:
      - Discord__Token
    ports:
      - "127.0.0.1:8080:8080"
    networks:
      - postgres

secrets:
  Discord__Token:
    file: ./discord-token.txt

networks:
  postgres:
    external: true
    name: postgres_default
```

- `image`: a release tag (`X.Y.Z`). `unstable` changes with each push to `main` ([Container](#container)).
- `Custos__PublicBaseUrl`: the origin that Custos and Keycloak use to reach the server. The client ID of Custos is `{PublicBaseUrl}/.well-known/custos-client`. The server makes the URLs of Custos from this value, not from the request.
- `Custos__Authority`: the issuer of the realm, exactly as the `iss` claim of a token has it. The server gets the metadata and the signing keys of Keycloak from this URL, so the container must reach it.
- `ConnectionStrings__custos`: an Npgsql connection string. Put the password in a `.env` file next to the Compose file (`CUSTOS_DB_PASSWORD=...`). Do not commit that file.
- `secrets`: the bot token, in the file `discord-token.txt` next to the Compose file. Compose mounts it as `/run/secrets/Discord__Token`, and the server reads each file of `/run/secrets` as a configuration key. `__` in a file name is `:`. Do not commit that file. Without the secret, the Discord connection is off.
- `Mcp__Enabled` and `Mcp__Resource`: the MCP endpoint ([MCP endpoint](#mcp-endpoint)). Leave them out for a server without MCP.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: the server applies `X-Forwarded-Proto` and `X-Forwarded-For` of the proxy. The metadata document of the MCP endpoint needs the public scheme. The server then trusts these headers from each client that reaches port 8080, so only the proxy must reach it.
- `OTEL_*`: the telemetry ([Telemetry and health](#telemetry-and-health)). Without `OTEL_EXPORTER_OTLP_ENDPOINT`, the server exports nothing.
- `networks`: an existing network that PostgreSQL is on. `docker network ls` shows its name. If PostgreSQL runs on another host, remove `networks` and use that host name in `Host=`.
- `ports`: the reverse proxy on the host connects to `127.0.0.1:8080`. If the proxy runs in Docker, put the server on the network of the proxy instead.

The other settings are in [Configuration](#configuration). Set them as `Custos__<Key>`.

### 4. Configure the reverse proxy

Send all paths of `https://custos.example.com` to port 8080 of the container. Keycloak gets `/.well-known/custos-client` through this proxy, so the certificate must be valid for Keycloak. Keep the `Host` header of the request, and send `X-Forwarded-Proto` and `X-Forwarded-For`.

### 5. Start and check the server

```sh
docker compose up -d
docker compose logs -f custos-server
curl https://custos.example.com/health/ready
curl https://custos.example.com/.well-known/custos-configuration
```

1. `/health/ready` must give `Healthy`. It checks the database.
2. The discovery document must show the public URL in `upload_endpoint` and `client_id`.
3. Set the URL of the server in Custos, and log in. Keycloak shows a consent page on the first login.
4. After an upload, the table `wars` has the current war, and the table `uploads` has one row.
5. With the bot token, the log has no warning about `Discord:Token`. In a guild, `/custosadmin settings view` shows the settings.
6. With MCP, `curl https://custos.example.com/.well-known/oauth-protected-resource` gives the metadata document with `"resource": "https://custos.example.com/mcp"`.

### Operation

- Run one instance. The rate limits, the cache of the war API, and the Discord gateway are in the process.
- The image has a `HEALTHCHECK`. It runs `dotnet Custos.Server.dll --healthcheck`, because the image has no shell and no `curl` ([Telemetry and health](#telemetry-and-health)). The check uses `/health/alive`. Check `/health/ready` from the proxy or from a monitor.
- Back up the database before you update the image. The server applies new migrations when it starts ([Migrations](#migrations)).
- The archive and the purge of a war are manual ([Wars](#wars)).
