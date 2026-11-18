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
| `DevelopmentGuildId` | (none) | The guild of an upload whose token has no claim `active_guild`. The server uses it only in the environment `Development`. `1236389163276959888` in development. |
| `WarApiBaseUrl` | `https://war-service-live.foxholeservices.com` | The origin of the Foxhole war API. Each shard has its own host. The server gets the current war from `/api/worldconquest/war` on it. If the API does not answer, the server uses the last answer. Without an earlier answer, an upload gets `503`. |
| `Shard` | `live` | A stable label for the shard of `WarApiBaseUrl`. The war API does not label its shard. The label is a part of the ID of each structure, so do not change it during a war. The server supports one shard. |
| `WarPollIntervalSeconds` | `300` | The time between two requests for the current war. `0` stops the requests. |

## Guilds

The server is multi-tenant. A tenant is a Discord guild. The access token names the guild of the user in the claim `active_guild`, as the guild ID in a string. A custom authenticator in Keycloak lets the user choose the guild and sets the claim. That authenticator is not part of this repository.

An upload without a valid `active_guild` gets `403`. In the environment `Development`, `DevelopmentGuildId` supplies the guild when the claim is absent. For production, see [The claim `active_guild`](#the-claim-active_guild).

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
- The development realm permits HTTP. A production realm must use HTTPS for the client ID ([Prepare Keycloak](#2-prepare-keycloak)).
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

## Deploy

This section deploys the container with Docker Compose against an existing PostgreSQL and an existing Keycloak. The examples use these values. Replace them with your own.

| Value | Example |
|---|---|
| Public URL of the server | `https://custos.example.com` |
| Issuer of the Keycloak realm | `https://id.example.com/realms/custos` |
| Database, role | `custos`, `custos` |

The server needs:

- PostgreSQL 18. The tests and the AppHost use 18 only. The server uses list partitions and advisory locks.
- Keycloak 26.8 or later, with the feature `cimd`.
- A reverse proxy that terminates TLS. The container serves HTTP on port 8080.

### 1. Prepare PostgreSQL

Create a role and a database that the role owns. The server applies its migrations when it starts, so the role must be able to create tables in the database.

```sql
CREATE ROLE custos LOGIN PASSWORD 'change-me';
CREATE DATABASE custos OWNER custos;
```

The role needs no other privilege. Make sure that the container can reach PostgreSQL, for example through a shared Docker network (step 3).

### 2. Prepare Keycloak

1. Start Keycloak with the feature `cimd`: the option `--features=cimd`, or the environment variable `KC_FEATURES=cimd`. Keep other features that you already enable in the same list.
2. Select a realm for Custos, or create one. The issuer of the realm is the value of `Custos__Authority`.
3. Add the client profile and the client policy of Custos to the realm (see below).
4. Set the session lifetime of the realm. Keycloak refuses `offline_access` for a CIMD client, so the SSO session sets how long a login of Custos lasts. The development realm uses 7 days idle (`ssoSessionIdleTimeout` 604800) and 30 days at most (`ssoSessionMaxLifespan` 2592000).
5. Add the claim `active_guild` (see below).

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

### 3. Write the Compose file

```yaml
services:
  custos-server:
    image: ghcr.io/atlashq-gg/custos-server:unstable
    restart: unless-stopped
    environment:
      Custos__PublicBaseUrl: https://custos.example.com
      Custos__Authority: https://id.example.com/realms/custos
      ConnectionStrings__custos: >-
        Host=postgres;Port=5432;Database=custos;Username=custos;Password=${CUSTOS_DB_PASSWORD}
    ports:
      - "127.0.0.1:8080:8080"
    networks:
      - postgres

networks:
  postgres:
    external: true
    name: postgres_default
```

- `image`: pin a release tag (`X.Y.Z`) or a `sha-<commit>` tag. `unstable` changes with each push to `main` ([Container](#container)).
- `Custos__PublicBaseUrl`: the origin that Custos and Keycloak use to reach the server. The client ID of Custos is `{PublicBaseUrl}/custos/client-metadata.json`. The server makes all its URLs from this value, not from the request, so the proxy needs no forwarded headers.
- `Custos__Authority`: the issuer of the realm, exactly as the `iss` claim of a token has it. The server gets the metadata and the signing keys of Keycloak from this URL, so the container must reach it.
- `ConnectionStrings__custos`: an Npgsql connection string. Put the password in a `.env` file next to the Compose file (`CUSTOS_DB_PASSWORD=...`). Do not commit that file.
- `networks`: an existing network that PostgreSQL is on. `docker network ls` shows its name. If PostgreSQL runs on another host, remove `networks` and use that host name in `Host=`.
- `ports`: the reverse proxy on the host connects to `127.0.0.1:8080`. If the proxy runs in Docker, put the server on the network of the proxy instead.

The other settings are in [Configuration](#configuration). Set them as `Custos__<Key>`.

### 4. Configure the reverse proxy

Send all paths of `https://custos.example.com` to port 8080 of the container. Keycloak gets `/custos/client-metadata.json` through this proxy, so the certificate must be valid for Keycloak.

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

### Operation

- Run one instance. The rate limit and the cache of the war API are in the memory of the process.
- The image has no shell and no `curl`, so a Compose `healthcheck` cannot run in it. Check `/health/ready` from the proxy or from a monitor.
- Back up the database before you update the image. The server applies new migrations when it starts.
- The archive and the purge of a war are manual ([Wars](#wars)).
