# Changelog

## Unreleased

- Events: after an upload changes a stockpile, the server publishes one event of the type `stockpile.changed` for each guild that can read the stockpile, with the text of the change and the access policy of that guild. It publishes to NATS JetStream with `ConnectionStrings:events`, as `optio/docs/producers.md` describes. An upload does not wait for the broker. The metric `custos.events` counts the events by outcome.
- Telemetry: the service name is `Custos`, unless `OTEL_SERVICE_NAME` sets another.

## 0.2.0

The server connects to Discord as a bot, gives access by the roles of a member, labels the structures, and serves an MCP endpoint for searches. Contract version `v0` does not change.

- Telemetry: OpenTelemetry traces, metrics, and logs, with OTLP export when the environment has `OTEL_EXPORTER_OTLP_ENDPOINT`. One activity source and one meter, `Custos.Server`. Instruments for uploads, the war API, Discord requests, and MCP tool calls.
- Health checks with the tags `live` and `ready`. The flags `--healthcheck` and `--version`, and a `HEALTHCHECK` in the image.
- The standard resilience handler for the war API, with 15 seconds at most.
- Discord connection with `Discord:Token`, through the gateway with the intent `Guilds`. Without the token, the server works as 0.1.0. The server reads the file `/run/secrets/Discord__Token`.
- `/custosadmin settings view` and `update`: the settings document of a guild with the roles that can report, read, and label, and the guilds that can read the records. The table `guild_settings`.
- An upload needs a member of the guild with the access `report`, with the Discord user ID in the claim `discord_id`. `403` for a refusal, and `503` when Discord does not answer. The column `uploads.discord_user_id`.
- Structure labels: the server generates a unique label for each structure from the place, the type, and a code. `/custos label` changes a label. The server generates the labels of the rows of 0.1.0 when it starts.
- Map data of the war API: the place names and the map items of each region for each war.
- The catalog of the game: its format, its loader, the script that makes it from the Foxhole wiki, and the file with 688 entries. An entry has the crate size (the number in an item crate, or 3 for a vehicle or a structure that the Mass Production Factory can produce) and the pallet size when known. The search tools show both. The file is under CC BY-NC-SA 4.0, so the image licenses are `AGPL-3.0-or-later AND CC-BY-NC-SA-4.0`. Nobody can use the image with the file for a commercial purpose ([catalog/LICENSE.md](catalog/LICENSE.md)).
- MCP endpoint `/mcp` with `Mcp:Enabled`: an OAuth resource server with the metadata document, the scopes `custos:read` and `custos:write`, a guard on each tool, and a rate limit for each caller. The tools `list_guilds`, `find_items`, `find_structures`, and `set_structure_label`. A search also reads the records of the guilds that share with the guild.
- One audit line in the log for each settings change, label change, and MCP tool call.
- Development realm: the user attribute `discord_id`, the client scopes `basic`, `custos-discord`, `custos:read`, and `custos:write`, the policies for the registration of a harness, and PKCE with S256. Keycloak starts with `cimd,resource-indicators`.

## 0.1.0

First version. Implements contract version `v0`.

- Discovery document from the configuration, with a link to the OpenID configuration of the authorization server.
- Client ID Metadata Document of Custos at `/.well-known/custos-client`. Its URL is the client ID.
- Upload endpoint: size limit, rate limit, and a parser for the map data file.
- The upload requires the claim `active_guild` in the access token, and answers `403` without it. In development, `DevelopmentGuildId` supplies the guild.
- PostgreSQL through EF Core, with migrations that the server applies when it starts.
- Storage for each guild and war: one record for each upload, the current state of structures and stockpiles, and their history. The upload result lists the stockpiles.
- Entity IDs as UUID version 5, so that each guild calculates the same IDs.
- War monitor: it records each new war from the Foxhole war API, creates its history partitions, and ends the earlier war. The server ignores a pinned item from before the start of the war.
- When the war API does not answer, the server uses the last answer. Without an earlier answer, the upload answers `503`.
- `GET /health/alive` for liveness, and `GET /health/ready` with the database.
- OpenAPI document and Scalar page. The document has the version of the build, and the server logs the version when it starts.
- Aspire AppHost with PostgreSQL 18, Keycloak 26.8.2, and a development realm that accepts the CIMD client.
- Container image `ghcr.io/atlashq-gg/custos-server`. Its labels have the version of the build and the license AGPL-3.0-or-later.
