# Custos reference server

Version 0.1.0. Contract version `v0`. License: AGPL-3.0-or-later ([LICENSE](LICENSE)).

The reference implementation of the [Custos specification](../spec/README.md). It serves the discovery document and the Client ID Metadata Document of Custos, and it accepts uploads. It parses the map data file and keeps the parsed data in PostgreSQL, for each Discord guild. It does not keep the file.

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
| Dashboard | `http://localhost:15180` | The Aspire dashboard. The login URL is in the console output. |

Keycloak imports [the realm](Custos.Server.AppHost/keycloak/custos-realm.json) on each start. Changes in the Keycloak console last for one run.

## Endpoints

| Endpoint | Content |
|---|---|
| `GET /.well-known/custos-configuration` | The discovery document ([spec](../spec/01-discovery.md)). |
| `GET /custos/client-metadata.json` | The Client ID Metadata Document (CIMD) of Custos. Its URL is the client ID. |
| `POST /api/v1/uploads` | The upload ([spec](../spec/03-upload.md)). It parses the file, keeps the data for the guild of the user, and returns the stockpiles in the file. |
| `GET /openapi/v1.json` | The OpenAPI document, generated from the code. |
| `GET /docs` | The Scalar page for the OpenAPI document. |
| `GET /health/alive` | The liveness check. It does not check the database. |
| `GET /health/ready` | The readiness check, with the database. |

## Configuration

The section `Custos` in `appsettings.json` or environment variables (`Custos__PublicBaseUrl`). The connection string of PostgreSQL is `ConnectionStrings:custos` (`ConnectionStrings__custos`). The AppHost sets it.

| Key | Default | Content |
|---|---|---|
| `PublicBaseUrl` | (required) | The origin of the server as Custos sees it. |
| `Authority` | (required) | The issuer URL of the authorization server. |
| `OpenIdConfiguration` | `{Authority}/.well-known/openid-configuration` | The metadata document of the authorization server, as the discovery document links it. |
| `ClientId` | `{PublicBaseUrl}/custos/client-metadata.json` | The client ID of Custos. |
| `ServiceName` | `Custos reference server` | The name that Custos shows. |
| `Scopes` | `["openid"]` | The scopes that Custos requests. |
| `UploadMaxBytes` | `4194304` | The largest upload body. |
| `UploadMinIntervalSeconds` | `60` | The shortest time between two uploads of one user. `10` in development. |
| `DevelopmentGuildId` | (none) | The guild of an upload whose token has no claim `active_guild`. The server uses it only in the environment `Development`. `1` in development. |
| `WarApiBaseUrl` | `https://war-service-live.foxholeservices.com` | The origin of the Foxhole war API. Each shard has its own host. The server gets the current war from `/api/worldconquest/war` on it. If the API does not answer, the server uses the last answer. Without an earlier answer, an upload gets `503`. |
| `Shard` | `live` | A stable label for the shard of `WarApiBaseUrl`. The war API does not label its shard. The label is a part of the ID of each structure, so do not change it during a war. The server supports one shard. |
| `WarPollIntervalSeconds` | `300` | The time between two requests for the current war. `0` stops the requests. |

## Guilds

The server is multi-tenant. A tenant is a Discord guild. The access token names the guild of the user in the claim `active_guild`, as the guild ID in a string. A custom authenticator in Keycloak lets the user choose the guild and sets the claim. That authenticator is not part of this repository.

An upload without a valid `active_guild` gets `403`. In the environment `Development`, `DevelopmentGuildId` supplies the guild when the claim is absent.

## Storage

The server keeps the parsed data in PostgreSQL. It applies its migrations when it starts. Issue #1 holds the design ("Extension: guild storage in `server/`").

- `guild_id`, then `war_id`, start the key of each table of guild data. `wars` is the only table without `guild_id`.
- The current state is in `structures`, `stockpiles`, and `stockpile_entries`. The history is in `structure_changes` and `stockpile_changes`. The history keeps a change, not a sighting: for equal content, the server only moves `last_seen_at`. Data that is not newer than the last sighting changes nothing.
- The ID of a structure is a UUID version 5 of the shard, the war, the region, and the coordinates as exact float32. The ID of a stockpile is a UUID version 5 of the structure and the name. Thus each guild calculates the same IDs ([EntityIds.cs](Custos.Server/Storage/EntityIds.cs)).
- The server keeps a public stockpile only when it has entries, or when the server already knows it.
- The server ignores a pinned item with `LastUpdated` before the start of the war.

### Wars

The war monitor asks the war API for the current war on an interval. An upload does the same. For a new war, the server adds the row to `wars`, creates the partitions of the two history tables, and sets `ended_at` on the earlier war. A partition name has the war number, for example `stockpile_changes_w142`. The archive and the purge of a war are not automated.

### Migrations

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project Custos.Server --output-dir Storage/Migrations
```

EF Core cannot declare a partitioned table. A migration that creates or changes a history table must use SQL for it, as the migration `Initial` does.

## Authentication with Keycloak

Keycloak 26.8 has the experimental feature `cimd`. The realm has a client policy that accepts a client ID that is a URL on the host of the server. Keycloak then gets the registration of Custos from the CIMD document.

- Keycloak must reach the server at the client ID URL. In development, Keycloak runs in a container and uses the Aspire container tunnel, so the client ID is `http://aspire.dev.internal:5080/custos/client-metadata.json`.
- The policy permits the domain `oauth2callback`, because Keycloak reads the redirect URI `custos://oauth2callback` as a URL with that host.
- The development realm permits HTTP. A production realm must use HTTPS for the client ID.
- A token for Custos has no audience. The server requires the claim `azp` to be the client ID.
- Keycloak shows a consent page for the client on the first login.

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

To build and run the image locally:

```sh
docker build -t custos-server .
docker run -p 8080:8080 -e Custos__PublicBaseUrl=https://custos.example.com -e Custos__Authority=https://id.example.com/realms/custos -e ConnectionStrings__custos="Host=db;Database=custos;Username=custos;Password=..." custos-server
```
