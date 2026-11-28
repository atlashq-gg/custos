# Agents: deploy

- This folder holds the files of the deployment of the full system, the guide [README.md](README.md), and the one composition of the full system in [system/](system/README.md) ([ADR 0008](../docs/adr/0008-one-composition-of-the-full-system.md)). It is not a project: it has no version and no changelog. The workflow `deploy` builds and tests `system/`, and examines `compose.yaml`.
- Start the full system for development, debugging, or a test across the services only through `system/Custos.AppHost`. Do not add a second composition.
- Add a system test only for behavior that needs more than one service. A system test acts only through public interfaces: the contract of `spec/` as a client, and the interfaces of the domain services.
- Keep each parameter of an outside account (Discord, the model provider) optional in the AppHost. The system tests start the system without them.
- Keep the fixed ports in `system/Custos.AppHost/AppHost.cs` in step with the launch profiles of Optio and the Server.
- In `system/`, run `dotnet csharpier format .` before a commit. Keep each line at 100 characters or less, comments and strings too. CI checks both.
- Run `dotnet test system/Custos.System.slnx` before a commit that changes `system/`, `keycloak/`, or `nats/`. The tests need Docker, and the AppHost must not run.
- Keep [keycloak/README.md](keycloak/README.md) in step with the realm in `keycloak/custos-realm.json`. A production realm gets each setting through that document. Each change of a Keycloak setting also needs a line in the `CHANGELOG.md` of the project that starts with `Keycloak:` and names the step.
- Keep the realm in `keycloak/custos-realm.json` in step with the tests `KeycloakLoginTests` and `TokenExchangeTests` of the Server and `KeycloakLoginTests` of Optio. The tests and the AppHost of `system/` read the same file.
- Keep `nats/nats.conf` in step with [optio/docs/producers.md](../optio/docs/producers.md). The tests of the Server and of Optio, and the AppHost of `system/`, use the same file.
- Keep `compose.yaml`, `.env.example`, and the files in `keycloak/` in step with the configuration of each service and with the guide.
- Make sure that `compose.yaml` is valid: run `docker compose --env-file .env.example config --quiet`. CI runs the same check.
- Keep the license table of [README.md](README.md#license) correct for each new file.
