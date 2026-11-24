# optio

`optio` is the service that Custos calls ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). It implements the contract of [spec/](../spec/README.md): the discovery document, the upload, the stream, and the query. It calls the domain services, for example the [server](../server/README.md), with a token of the user for each service.

The work is in progress. The roadmap is [#48](https://github.com/atlashq-gg/custos/issues/48). This version serves the discovery document and the Client ID Metadata Document of Custos, and relays the upload. The stream and the query come later.

## Run

Requirements: .NET SDK 10 and Docker.

```sh
dotnet run --project Optio.AppHost
```

The AppHost starts the full system:

| Resource | URL | Content |
|---|---|---|
| optio | `http://localhost:5090` | The service URL for Custos. |
| server | `http://localhost:5080` | The domain service of the guild records ([server/README.md](../server/README.md)). |
| PostgreSQL 18 | (dashboard) | The database of the server. The data lasts for one run. |
| Keycloak | `http://localhost:8180` | The realm `custos` from [server/keycloak/custos-realm.json](../server/keycloak/custos-realm.json), with the user `dev`, password `dev`. |
| Dashboard | `http://localhost:15180` | The Aspire dashboard. The login URL is in the console output. |

The server needs a Discord bot token in the user secrets of the AppHost ([server/README.md](../server/README.md#run)). The AppHost makes a new secret for the client `optio` on each start.

## Endpoints

| Endpoint | Content |
|---|---|
| `GET /.well-known/custos-configuration` | The discovery document ([spec](../spec/01-discovery.md)). |
| `GET /.well-known/custos-client` | The Client ID Metadata Document (CIMD) of Custos. Its URL is the client ID. |
| `POST /api/v1/uploads` | The upload ([spec](../spec/03-upload.md)), relayed to the upload API of the server. |
| `GET /health/alive`, `GET /health/ready` | The health checks. |

## Upload

1. Custos logs in with the scopes of the discovery document. The scope `custos:client` gives its token the audience `optio`.
2. `optio` accepts a token with the audience `optio` and the client of Custos in `azp`.
3. `optio` examines the content type (`415`) and the size (`413`).
4. `optio` exchanges the token for a token of the server with the scope `custos:write` (RFC 8693). It keeps the exchanged token in memory until 30 seconds before it expires.
5. `optio` sends the body as a stream to the upload API of the server. It gives back the status, the body, and `Retry-After` of the server with no change.

When the authorization server refuses the exchange, or the server does not answer, the upload gets `503`. The server applies the upload interval and the Discord roles.

## Producers

A domain service sends events to `optio` through a RabbitMQ broker. The contract for such a producer is [docs/producers.md](docs/producers.md), with the schemas in [schemas/](schemas/).

## Configuration

The section `Optio` in `appsettings.json` or environment variables (`Optio__PublicBaseUrl`). A secret can be a file in `/run/secrets`, for example `/run/secrets/Optio__OptioClientSecret`.

| Key | Default | Content |
|---|---|---|
| `PublicBaseUrl` | (required) | The origin of `optio` as Custos sees it. |
| `Authority` | (required) | The issuer URL of the authorization server. |
| `OpenIdConfiguration` | `{Authority}/.well-known/openid-configuration` | The metadata document of the authorization server. |
| `CustosClientId` | `{PublicBaseUrl}/.well-known/custos-client` | The client ID of Custos. |
| `OptioClientId` | `optio` | The client ID of `optio`, and the audience of a token of Custos. |
| `OptioClientSecret` | (required) | The secret of the client `optio`. |
| `UploadApiUrl` | (required) | The upload API of the server, for example `http://server:8080/api/v1/uploads`. |
| `UploadScope` | `custos:write` | The scope of the token for the upload API. |
| `ServiceName` | `optio` | The name that Custos shows. |
| `ServiceDocumentation` | (none) | A page for humans about the service. |
| `Scopes` | `["openid", "custos:client"]` | The scopes that Custos requests. |
| `UploadMaxBytes` | `4194304` | The largest upload body. It must be equal to the value of the server. |
| `UploadMinIntervalSeconds` | `60` | The upload interval in the discovery document. It must be equal to the value of the server. |

## Health and telemetry

| Endpoint | Content |
|---|---|
| `GET /health/alive` | `Healthy` while the process runs. |
| `GET /health/ready` | `Healthy` when `optio` can serve requests. |

- `optio --version` prints the version.
- `optio --healthcheck` asks `/health/alive` on this machine. The exit code is 0 for a healthy service. The `HEALTHCHECK` of the image uses it.
- `optio` exports traces, metrics, and logs with OTLP when the environment has `OTEL_EXPORTER_OTLP_ENDPOINT`. Its activity source and its meter have the name `Optio`.

## Format

```sh
dotnet tool restore
dotnet csharpier format .
```

CSharpier does not wrap comments and strings. CI fails on a line of more than 100 characters.

## Tests

```sh
dotnet test Optio.slnx
```

## Container

The image is `ghcr.io/atlashq-gg/custos-optio`. The container serves HTTP on port 8080.

| Tag | Source |
|---|---|
| `X.Y.Z`, `X.Y` | The Git tag `optio-X.Y.Z`. |
| `unstable` | Each push to `main` that changes `optio/`. |
| `sha-<commit>` | Each push to `main` that changes `optio/`. |

```sh
docker build -t custos-optio .
docker run --rm -p 8080:8080 custos-optio
```

## License

AGPL-3.0-or-later ([LICENSE](LICENSE)).
