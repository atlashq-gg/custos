# Changelog

## Unreleased

- The upload requires the claim `active_guild` in the access token, and answers `403` without it. In development, `DevelopmentGuildId` supplies the guild.
- PostgreSQL through EF Core, with migrations that the server applies when it starts. The AppHost starts PostgreSQL 18.
- Parser for the map data file. The upload keeps one record for each upload, the current state of structures and stockpiles, and their history, keyed by guild and war. The upload result lists the stockpiles.
- Entity IDs as UUID version 5, so that each guild calculates the same IDs.
- War monitor: it records each new war, creates its history partitions, and ends the earlier war. The server ignores a pinned item from before the start of the war.
- When the war API does not answer, the server uses the last answer. Without an earlier answer, the upload answers `503`.
- `GET /health/ready` checks the database. `GET /health/alive` does not.

## 0.1.0

First version. Implements contract version `v0`.

- Discovery document from the configuration, with a link to the OpenID configuration of the authorization server.
- Client ID Metadata Document of Custos.
- Placeholder upload endpoint: GVAS check, SHA-256, size limit, rate limit, current war from the Foxhole war API.
- OpenAPI document and Scalar page.
- Aspire AppHost with Keycloak 26.8 and a development realm that accepts the CIMD client.
