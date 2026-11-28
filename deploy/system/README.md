# The full system

This folder holds the one composition of the full system for development, debugging, and the tests across the services ([ADR 0008](../../docs/adr/0008-one-composition-of-the-full-system.md)). It has no version and no release.

| Path | Content |
|---|---|
| `Custos.AppHost/` | The Aspire AppHost. It starts each part of the full system. |
| `Custos.SystemTests/` | The system tests. They start the AppHost and act as a client of [spec/](../../spec/README.md). |
| `Custos.System.slnx` | The solution: the AppHost, the system tests, [Optio](../../optio/README.md), and the [Server](../../server/README.md). |

## Run

Requirements: .NET SDK 10 and Docker. From the root folder of the repository:

```sh
dotnet run --project deploy/system/Custos.AppHost
```

The AppHost starts these resources:

| Resource | URL | Content |
|---|---|---|
| optio | `http://localhost:5090` | Optio, the service URL for Custos. |
| server | `http://localhost:5080` | The Server, the domain service of the guild records. |
| PostgreSQL 18 | (dashboard) | The databases of the Server and of Optio. The data lasts for one run. |
| events | (dashboard) | The NATS broker with JetStream, with the users of [nats/nats.conf](../nats/nats.conf). The Server publishes its events to it, and Optio makes the stream. The events last for one run. |
| keycloak | `http://localhost:8180` | The realm `custos` from [keycloak/custos-realm.json](../keycloak/custos-realm.json), with the user `dev`, password `dev`. |
| Dashboard | `http://localhost:15180` | The Aspire dashboard. The login URL is in the console output. |

The ports are fixed. In development mode, Keycloak writes the URL that the client used into the `iss` claim, and each service compares `iss` with its authority exactly.

Keycloak imports the realm on each start. Changes in the Keycloak console last for one run. The AppHost makes a new secret for the client `optio` and a new password for each broker user on each start.

## Parameters

Each parameter is optional. Without a parameter, the system starts without the account of that parameter. Set a parameter in the user secrets of the AppHost:

```sh
dotnet user-secrets set Parameters:<name> <value> --project deploy/system/Custos.AppHost
```

| Parameter | Content | Without it |
|---|---|---|
| `discord-token` | The bot token of the Discord application of the Server. The token must have the correct format. | The Server has no Discord connection and no MCP endpoint. An upload needs no role. |
| `discord-user-id` | Your Discord user ID. The Server and Optio use it for a token without the claim `discord_id`. The realm gives the user `dev` no Discord user ID. | A token without the claim has no Discord user ID. |
| `optio-discord-token` | The bot token of the Discord application of Optio. In development, it can be the token of the bot of the Server. | Optio has no stream. |
| `claude-api-key` | A key of the Claude API. The AppHost then gives Optio the example system prompt, and the Server as the MCP service `custos` with its read scope and its write scope. | Optio has no query. |

## Debug

Open `Custos.System.slnx` in an IDE with Aspire support, for example Visual Studio or Rider with its Aspire plugin. Start the project `Custos.AppHost` with the debugger. The IDE attaches the debugger to Optio and to the Server, so a breakpoint in each service stops the same session.

To debug Custos against the full system, do the steps in [custos/README.md](../../custos/README.md#development).

## System tests

Requirements: .NET SDK 10 and Docker. Stop the AppHost first: the tests use the same fixed ports.

```sh
dotnet test deploy/system/Custos.System.slnx
```

The tests start the full system one time for all tests, without the parameters. A failed start writes the log of each resource to the test output.

- Add a test here only for behavior that needs more than one service. Each service keeps its own tests.
- A test acts on the system only through public interfaces: the contract of `spec/` as a client, and the interfaces of the domain services.

CI runs the system tests after each merge to `main`, and on a pull request with the label `system-tests`. It also runs them on a pull request that changes `spec/`, `deploy/`, or `optio/schemas/` ([GOVERNANCE.md](../../GOVERNANCE.md#ci-and-releases)).
