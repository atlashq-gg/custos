# Custos reference server

Version 0.1.0. Contract version `v0`. License: AGPL-3.0-or-later ([LICENSE](LICENSE)).

The reference implementation of the [Custos specification](../spec/README.md). It serves the discovery document and the Client ID Metadata Document of Custos, and it accepts uploads. It does not parse or keep the uploaded files: the upload endpoint is a placeholder.

## Run

Requirements: .NET SDK 10 and Docker.

```sh
dotnet run --project Custos.Server.AppHost
```

The AppHost starts:

| Resource | URL | Content |
|---|---|---|
| Server | `http://localhost:5080` | The service URL for Custos. |
| Keycloak | `http://localhost:8180` | The realm `custos` with the user `dev`, password `dev`. Admin user `admin`. Its password is in the dashboard. |
| Dashboard | `http://localhost:15180` | The Aspire dashboard. The login URL is in the console output. |

Keycloak imports [the realm](Custos.Server.AppHost/keycloak/custos-realm.json) on each start. Changes in the Keycloak console last for one run.

## Endpoints

| Endpoint | Content |
|---|---|
| `GET /.well-known/custos-configuration` | The discovery document ([spec](../spec/01-discovery.md)). |
| `GET /custos/client-metadata.json` | The Client ID Metadata Document (CIMD) of Custos. Its URL is the client ID. |
| `POST /api/v1/uploads` | The upload ([spec](../spec/03-upload.md)). It checks the GVAS header and returns the SHA-256 and the current war. The stockpile list is empty. |
| `GET /openapi/v1.json` | The OpenAPI document, generated from the code. |
| `GET /docs` | The Scalar page for the OpenAPI document. |
| `GET /health/alive` | The liveness check. |

## Configuration

The section `Custos` in `appsettings.json` or environment variables (`Custos__PublicBaseUrl`).

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
| `WarApiBaseUrl` | `https://war-service-live.foxholeservices.com` | The origin of the Foxhole war API. Each shard has its own host. The server gets the current war from `/api/worldconquest/war` on it. |

## Authentication with Keycloak

Keycloak 26.8 has the experimental feature `cimd`. The realm has a client policy that accepts a client ID that is a URL on the host of the server. Keycloak then gets the registration of Custos from the CIMD document.

- Keycloak must reach the server at the client ID URL. In development, Keycloak runs in a container and uses the Aspire container tunnel, so the client ID is `http://aspire.dev.internal:5080/custos/client-metadata.json`.
- The policy permits the domain `oauth2callback`, because Keycloak reads the redirect URI `custos://oauth2callback` as a URL with that host.
- The development realm permits HTTP. A production realm must use HTTPS for the client ID.
- A token for Custos has no audience. The server requires the claim `azp` to be the client ID.
- Keycloak shows a consent page for the client on the first login.

## Tests

```sh
dotnet test
```

The tests check the JSON documents against the schemas in [spec/schemas](../spec/schemas/). The test in the category `Integration` starts Keycloak with Testcontainers and does the full login. It needs Docker. Use `--filter "Category!=Integration"` to skip it.

## Container

```sh
docker build -t custos-server .
docker run -p 8080:8080 -e Custos__PublicBaseUrl=https://custos.example.com -e Custos__Authority=https://id.example.com/realms/custos custos-server
```
