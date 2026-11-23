# optio

`optio` is the service that Custos calls ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). It implements the contract of [spec/](../spec/README.md): the discovery document, the upload, the stream, and the query. It calls the domain services, for example the [server](../server/README.md), with a token of the user for each service.

The work is in progress. The roadmap is [#48](https://github.com/atlashq-gg/custos/issues/48). This version has the host, the health endpoints, and the telemetry only.

## Run

```sh
dotnet run --project Optio
```

The service listens on `http://localhost:5090`.

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
