# Deploy

This guide deploys the services of the system with Docker Compose: Optio, the Server, and the NATS broker. It uses an existing PostgreSQL and an existing Keycloak. Custos calls only Optio ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). Optio calls the Server with a token of the user, and reads the events of the Server from the broker ([ADR 0007](../docs/adr/0007-a-broker-carries-domain-events.md)).

This folder holds the files of the deployment: the Compose file, the configuration of NATS, and the setup of Keycloak with its files. The tests and the AppHost use the same configuration of NATS and the same development realm.

The options of each service are in [optio/README.md](../optio/README.md#configuration) and [server/README.md](../server/README.md#configuration). The examples use these values. Replace them with your own.

| Value | Example |
|---|---|
| Public URL of Optio, which Custos uses | `https://custos.example.com` |
| Public URL of the MCP endpoint of the Server | `https://records.example.com/mcp` |
| Issuer of the Keycloak realm | `https://id.example.com/realms/custos` |
| Database and role of the Server | `custos` |

The system needs:

- PostgreSQL 18. The tests and the AppHost use 18 only. The Server uses list partitions and advisory locks.
- Keycloak 26.8 or later, with the features `cimd` and `resource-indicators`.
- NATS 2.15 or later, with JetStream. The AppHost uses the image `nats:2.15.3-alpine`.
- A reverse proxy that terminates TLS. Each service container serves HTTP on port 8080.
- Two Discord applications, each with a bot: one for the Server, and one for Optio.

## 1. Prepare PostgreSQL

Create a role and a database for the Server. The Server applies its migrations when it starts, so the role must be able to create tables in its database. Optio has no database.

```sql
CREATE ROLE custos LOGIN PASSWORD 'change-me';
CREATE DATABASE custos OWNER custos;
```

The role needs no other privilege. Make sure that the Server container can reach PostgreSQL, for example through a shared Docker network (step 5).

## 2. Prepare Keycloak

Do each step of [keycloak/README.md](keycloak/README.md). It sets up each part of the realm that Custos, Optio, and the Server need:

1. The features `cimd` and `resource-indicators` of Keycloak 26.8 or later.
2. The session lifetime of the realm.
3. The user attribute `discord_id`.
4. The client scopes `custos-discord`, `custos-guild`, `custos:client`, `custos:read`, and `custos:write`.
5. The client `optio`. Copy its secret to `OPTIO_CLIENT_SECRET` in the file `.env` (step 5).
6. The client policies for the Client ID Metadata Document of Custos and of a harness, and PKCE.
7. The registration policy of a harness.
8. The values of each user.
9. The scopes of each other MCP service of Optio.
10. The checks.

The issuer of the realm is the value of `Authority` in the settings of each service (step 5).

To add the MCP endpoint of the Server to a harness, give it the URL of the endpoint as a remote MCP server with the HTTP transport. For example, in Claude Code:

```sh
claude mcp add --transport http custos https://records.example.com/mcp
```

The harness opens the login of Keycloak. The consent page shows the two scopes. A token with `custos:read` only does not list the write tools.

## 3. Prepare the Discord applications

Each service needs its own Discord application. The Server registers its commands when it starts, and a registration replaces all the commands of the application. Optio needs a privileged intent that the Server does not need.

1. For the Server, do the steps of [The Discord application](../server/README.md#the-discord-application).
2. For Optio, do the same steps with a second application. On the page **Bot**, also set **Server Members Intent** to on: Optio reads the roles of the members with an open stream. Keep the other privileged intents off.

Add the bot of Optio to each guild that uses the stream. A stream of a guild without the bot gets one error message.

## 4. Prepare NATS

The broker carries the events of the Server to Optio. The operator defines only the broker users and their permissions. Optio makes the stream of the events when it starts ([optio/docs/producers.md](../optio/docs/producers.md#broker)).

1. Use [nats/nats.conf](nats/nats.conf). The Compose file mounts it from `nats/` next to it. It turns on JetStream with the store `/data`, and defines the users `custos` and `optio` with their permissions.
2. Select a password for each user. Use only letters and digits, because the password is part of a URL in the configuration of each service. The NATS server reads the passwords from its environment variables `CUSTOS_PASSWORD` and `OPTIO_PASSWORD` (step 5).

The configuration defines no stream. For each new domain service that publishes events, add a user with the permissions of [optio/docs/producers.md](../optio/docs/producers.md#broker). Optio needs no change.

## 5. Write the Compose file

Copy this folder, or the files [compose.yaml](compose.yaml), [.env.example](.env.example), and [nats/nats.conf](nats/nats.conf) with the same layout. Then do these steps:

1. Copy `.env.example` to `.env`, and put the passwords and the secrets in it. Do not commit the file `.env`. Compose reads it next to the Compose file.
2. In `compose.yaml`, replace the example values: the image tags, the URLs in `server-settings` and `optio-settings`, and the network of PostgreSQL.
3. Make sure that the file is valid: `docker compose config --quiet`.

The parts of the Compose file:

- `image`: a release tag (`X.Y.Z`) or a pre-release tag (`X.Y.Z-<pre>`) of each service. The tags in the file are examples. `unstable` changes with each push to `main` ([optio/README.md](../optio/README.md#container), [server/README.md](../server/README.md#container)).
- `events`: the NATS server. Its service name is the host in the connection strings. The volume `events-data` keeps the events of the replay window over a restart of the broker. The broker needs no public route.
- `server-settings` and `optio-settings`: the settings of each service as JSON, in the sections `Custos` and `Mcp` of the Server ([server/README.md](../server/README.md#configuration)) and `Optio` of Optio ([optio/README.md](../optio/README.md#configuration)). Compose mounts each content as `/app/appsettings.Production.json` of its service, and the service reads it when it starts. Compose replaces `${...}` in the content, so do not put a `$` in a value.
- `PublicBaseUrl`: the origin that Custos and Keycloak use to reach Optio. The client ID of Custos is `{PublicBaseUrl}/.well-known/custos-client`.
- `Authority`: the issuer of the realm, exactly as the `iss` claim of a token has it. Each service gets the metadata and the signing keys of Keycloak from this URL, so each container must reach it.
- `Resource` of the Server: the public URL of the MCP endpoint. It is the audience of each token of the Server, also of a token that Optio gets by exchange. Thus it is the public URL even when Optio calls the Server on the internal network.
- `UploadApiUrl`: the internal URL of the upload API of the Server. The upload API needs no public route.
- `secrets`: Compose gets each secret from a variable of the file `.env` next to the Compose file, and mounts it as `/run/secrets/<name>`. Each service reads each file of `/run/secrets` as a configuration key, and `__` in a file name is `:`. Without `Discord__Token`, the Server has no Discord connection. Without `Optio__DiscordToken`, Optio has no stream.
- `ConnectionStrings__*`: the passwords come from the file `.env` too. Without `ConnectionStrings__events`, the Server publishes no events, and Optio has no stream. A service starts without the broker, and connects again by itself.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: each service applies `X-Forwarded-Proto` and `X-Forwarded-For` of the proxy. A service then trusts these headers from each client that reaches its port, so only the proxy must reach it.
- Telemetry: add `OTEL_EXPORTER_OTLP_ENDPOINT` to the environment of a service to export its traces, metrics, and logs. Without it, a service exports nothing. The service names are `Custos` for the Server and `Optio`. `OTEL_SERVICE_NAME` replaces a name.
- `networks`: an existing network that PostgreSQL is on. `docker network ls` shows its name. If PostgreSQL runs on another host, remove that network, and use the host name in `Host=`.
- `ports`: the reverse proxy on the host connects to `127.0.0.1:8080` for Optio and `127.0.0.1:8081` for the Server. If the proxy runs in Docker, put the services on the network of the proxy instead.

The value `UploadMaxBytes` must be equal in the two services. The value `UploadMinIntervalSeconds` too.

## 6. Configure the reverse proxy

| Public host | Paths | Target |
|---|---|---|
| `custos.example.com` | All | Optio, port 8080 |
| `records.example.com` | `/mcp` and `/.well-known/oauth-protected-resource` | The Server, port 8080 |

- Keep the `Host` header of the request, and send `X-Forwarded-Proto` and `X-Forwarded-For`.
- Keycloak gets `/.well-known/custos-client` through the proxy, so the certificate must be valid for Keycloak.
- The stream at `/api/v1/stream` is a long response with Server-Sent Events. Turn off the buffer of the response for this path, and let a response run for longer than the lifetime of an access token. Optio sends a comment 15 seconds after its last write, and closes the stream when the token expires.
- Give no public route to the other paths of the Server, and no route to the broker.

## 7. Start and check the system

```sh
docker compose up -d
docker compose logs -f optio server
curl https://custos.example.com/health/ready
curl https://custos.example.com/.well-known/custos-configuration
curl https://records.example.com/.well-known/oauth-protected-resource
```

1. `/health/ready` of each service must give `Healthy`. The Server checks its database. Readiness does not depend on the broker, because the upload works without it.
2. The log of Optio must show `The stream OPTIO of the events is ready`. A warning that the event broker is not available shows a wrong host, user, or password.
3. The discovery document must show the public URL in `upload_endpoint` and `client_id`. With the broker and the Discord token of Optio, it has `stream_endpoint`.
4. The metadata document of the Server must show `"resource": "https://records.example.com/mcp"`.
5. Set the URL of Optio in Custos, and log in. Keycloak shows a consent page on the first login.
6. After an upload that changes a stockpile, the log of the Server shows no warning of the event broker. A member with a read role of the guild gets the event on the stream.
7. With the Discord tokens, the logs have no warning about a token. In a guild, `/custosadmin settings view` works.

## Operation

- Run one instance of each service. The rate limits, the cache of the war API, the Discord gateways, and the count of open streams are in the memory of the process.
- Each image of a service has a `HEALTHCHECK` with the flag `--healthcheck`, because the images have no shell and no `curl`. The check uses `/health/alive`. Check `/health/ready` from the proxy or from a monitor.
- Back up the database of the Server before you update an image. The Server applies new migrations when it starts.
- The broker keeps the events of the replay window, 15 minutes by default, and removes each event at its age. Optio sets the age limit of the stream at each start, from `ReplayWindowMinutes`. Optio keeps no events. After a restart of Optio, a client that connects again with its position gets the events that it missed.
- When the broker is not available, an upload still succeeds, and its event can be lost. A stream request gets `503`, and Custos tries again later.
- The archive and the purge of a war are manual ([server/README.md](../server/README.md#wars)).

## License

Various. This folder has no license of its own. Each file keeps the license of the project that it came from:

| File | License | Source |
|---|---|---|
| `keycloak/custos-realm.json` | AGPL-3.0-or-later | `server/` |
| `nats/nats.conf` | AGPL-3.0-or-later | `optio/` |
| Each other file | MIT | The root of the repository |
