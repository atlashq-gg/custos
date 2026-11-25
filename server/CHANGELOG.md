# Changelog

## Unreleased

- The development realm moves to [deploy/keycloak/custos-realm.json](../deploy/keycloak/custos-realm.json), and the deployment guide to [deploy/README.md](../deploy/README.md). The tests read the realm and the NATS configuration from `deploy/`.

## 0.3.0-alpha.1

A pre-release.

The server is a domain service behind Optio ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). It publishes its stockpile changes to the event broker ([ADR 0007](../docs/adr/0007-a-broker-carries-domain-events.md)).

- Removed: the discovery document, the Client ID Metadata Document of Custos, and the options `PublicBaseUrl`, `ServiceName`, `Scopes`, `OpenIdConfiguration`, and `ClientId`. Optio serves them now.
- `Custos:Resource` replaces `Mcp:Resource`. The upload API and `/mcp` accept a token only with this audience. The upload API needs the scope `custos:write`, with no check of `azp`.
- Events: after the commit of an upload, the server publishes one event `stockpile.changed` for each changed stockpile and each guild that can read it. It publishes to NATS JetStream with `ConnectionStrings:events`, below the subject `events.custos.`, as `optio/docs/producers.md` describes. The event holds the text for a person and the access policy of the guild. An upload does not wait for the broker. The counter `custos.events` counts the events by outcome.
- The text of an event: the place, then at most 5 changes as the signed difference and the new total, for example `Rifle crates +20 (40)`, then `+N more`.
- The OpenTelemetry service name is `Custos` when `OTEL_SERVICE_NAME` does not set one.
- The MCP endpoint gives the card of the server in `instructions`, and the title `Custos`. A harness with more than one service gives the card to its model.
- A token with the read scope lists `set_structure_label` too, with its annotation `readOnlyHint` false. Its call still needs `custos:write`. Thus a harness knows the write tool before the user accepts a change.
- The realm file moves to `server/keycloak/custos-realm.json`. It has the client `optio` with the standard token exchange, and the optional scope `custos:client`. This change removes the AppHost of the server. The AppHost of Optio starts the full system.

For operators:

- The client ID of Custos is now a URL of Optio, so each user logs in again.
- `Custos__PublicBaseUrl` is gone. `Mcp__Resource` is now `Custos__Resource`.
- The scope `custos-guild` must also be a default scope of the client `optio`.
- [docs/deploy.md](../docs/deploy.md) deploys the full system, with the NATS broker.

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
- Development realm: the profile and the policy `custos-cimd` accept the Client ID Metadata Document of Claude Code. The permitted domains have `claude.ai` and the hosts of its redirect URIs, and `cimd-resource-indicator-allow-list` has the MCP resource. The user `dev` has the realm role `offline_access`.
- AppHost: the parameters `discord-token` and `discord-user-id`. The dashboard asks for a value that is not set. The server treats an empty `Discord:Token` as no token.

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
