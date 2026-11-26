# Agents: deploy

- This folder holds the files of the deployment of the full system, and the guide [README.md](README.md). It is not a project: it has no version, no changelog, and no CI of its own.
- Keep the realm in `keycloak/custos-realm.json` in step with the tests `KeycloakLoginTests` and `TokenExchangeTests` of the Server and `KeycloakLoginTests` of Optio. The tests and the AppHost read the same file.
- Keep `nats/nats.conf` in step with [optio/docs/producers.md](../optio/docs/producers.md). The tests of the Server and of Optio, and the AppHost, use the same file.
- Keep `compose.yaml`, `.env.example`, and the files in `keycloak/` in step with the configuration of each service and with the guide.
- Make sure that `compose.yaml` is valid: copy `.env.example` to `.env`, and run `docker compose config --quiet`.
- Keep the license table of [README.md](README.md#license) correct for each new file.
