# Changelog

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
