# Deploy

This guide deploys the services of the system with Docker Compose: Optio, the server, and the NATS broker. It uses an existing PostgreSQL and an existing Keycloak. Custos calls only Optio ([ADR 0006](adr/0006-optio-is-the-service-of-the-contract.md)). Optio calls the server with a token of the user, and reads the events of the server from the broker ([ADR 0007](adr/0007-a-broker-carries-domain-events.md)).

The options of each service are in [optio/README.md](../optio/README.md#configuration) and [server/README.md](../server/README.md#configuration). The examples use these values. Replace them with your own.

| Value | Example |
|---|---|
| Public URL of Optio, which Custos uses | `https://custos.example.com` |
| Public URL of the MCP endpoint of the server | `https://records.example.com/mcp` |
| Issuer of the Keycloak realm | `https://id.example.com/realms/custos` |
| Database and role of the server | `custos` |

The system needs:

- PostgreSQL 18. The tests and the AppHost use 18 only. The server uses list partitions and advisory locks.
- Keycloak 26.8 or later, with the features `cimd` and `resource-indicators`.
- NATS 2.15 or later, with JetStream. The AppHost uses the image `nats:2.15.3-alpine`.
- A reverse proxy that terminates TLS. Each service container serves HTTP on port 8080.
- Two Discord applications, each with a bot: one for the server, and one for Optio.

## 1. Prepare PostgreSQL

Create a role and a database for the server. The server applies its migrations when it starts, so the role must be able to create tables in its database. Optio has no database.

```sql
CREATE ROLE custos LOGIN PASSWORD 'change-me';
CREATE DATABASE custos OWNER custos;
```

The role needs no other privilege. Make sure that the server container can reach PostgreSQL, for example through a shared Docker network (step 5).

## 2. Prepare Keycloak

1. Start Keycloak with the features `cimd` and `resource-indicators`: the option `--features=cimd,resource-indicators`, or the environment variable `KC_FEATURES`. Keep other features that you already enable in the same list.
2. Select a realm for Custos, or create one. The issuer of the realm is the value of `Authority` in the settings of each service (step 5).
3. Set the session lifetime of the realm. Custos does not request `offline_access`, so the SSO session sets how long a login of Custos lasts. The development realm uses 7 days idle (`ssoSessionIdleTimeout` 604800) and 30 days at most (`ssoSessionMaxLifespan` 2592000).
4. Add the client profile and the client policy of Custos.
5. Add the claims `discord_id` and `active_guild`.
6. Add the scopes of the server and the scope `custos:client`.
7. Add the client `optio`.
8. For the MCP endpoint of the server, add the registration policies of a harness.

The [development realm](../server/keycloak/custos-realm.json) holds each of these settings. Use it as a reference.

### Client profile and client policy

Keycloak gets the registration of Custos from the Client ID Metadata Document of Optio, at `https://custos.example.com/.well-known/custos-client`. The profile accepts that document. The policy applies the profile to a client ID on the host of Optio. Put the host of Optio in both files. The domain `oauth2callback` permits the redirect URI `custos://oauth2callback`.

`profiles.json`:

```json
{
  "profiles": [
    {
      "name": "custos-cimd",
      "description": "Accepts the metadata document of Custos.",
      "executors": [
        {
          "executor": "client-id-metadata-document",
          "configuration": {
            "cimd-allow-http-scheme": "false",
            "cimd-allow-permitted-domains": ["custos.example.com", "oauth2callback"]
          }
        }
      ]
    }
  ]
}
```

`policies.json`:

```json
{
  "policies": [
    {
      "name": "custos-cimd",
      "description": "Clients whose ID is the URL of the metadata document on Optio.",
      "enabled": true,
      "conditions": [
        {
          "condition": "client-id-uri",
          "configuration": {
            "client-id-uri-scheme": ["https"],
            "client-id-uri-allow-permitted-domains": ["custos.example.com"]
          }
        }
      ],
      "profiles": ["custos-cimd"]
    }
  ]
}
```

CAUTION: Each command replaces all client profiles or all client policies of the realm. If the realm already has profiles or policies, get them first, and add the entries of Custos to the lists.

```sh
kcadm.sh config credentials --server https://id.example.com --realm master --user admin
kcadm.sh get realms/custos/client-policies/profiles
kcadm.sh get realms/custos/client-policies/policies
kcadm.sh update realms/custos/client-policies/profiles -f profiles.json
kcadm.sh update realms/custos/client-policies/policies -f policies.json
```

The admin console has the same settings in **Realm settings**, on the tab **Client policies**.

### The claim `discord_id`

The server and Optio need the Discord user ID in the claim `discord_id` of the access token. The development realm shows one method: a user attribute and a default client scope.

1. In **Realm settings**, on the tab **User profile**, create the attribute `discord_id`. Let only an administrator edit it. Keycloak removes a user attribute that the user profile does not declare.
2. In **Client scopes**, create the scope `custos-discord` with the protocol `OpenID Connect`. Set **Include in token scope** to off.
3. In the tab **Mappers** of the scope, configure a new mapper of the type **User Attribute**: **User Attribute** `discord_id`, **Token Claim Name** `discord_id`, **Claim JSON Type** `String`, **Add to access token** on.
4. In the list **Client scopes**, set the assigned type of `custos-discord` to **Default**.
5. Set the attribute `discord_id` of each user to the Discord user ID.

### The claim `active_guild`

Outside the environment `Development`, the services ignore `DevelopmentGuildId`. A request whose token has no claim `active_guild` gets `403`. The authenticator that lets a user choose a guild is not built yet ([#1](https://github.com/atlashq-gg/custos/issues/1)).

For one guild, a hardcoded claim gives each token of the realm the same guild:

1. In **Client scopes**, create the scope `custos-guild` with the protocol `OpenID Connect`. Set **Include in token scope** to off.
2. In the tab **Mappers** of the scope, configure a new mapper of the type **Hardcoded claim** with these values:
   - **Token Claim Name**: `active_guild`
   - **Claim value**: the guild ID
   - **Claim JSON Type**: `String`
   - **Add to access token**: on
3. In the list **Client scopes**, set the assigned type of `custos-guild` to **Default**. Keycloak then adds the scope to each new client, also to the client that it registers through CIMD.

If Keycloak registered the client of Custos before step 3, add the scope to that client in **Clients**, on the tab **Client scopes**. Remove the scope when the authenticator sets the claim.

### The scopes

| Scope | Assigned type | Mappers | Use |
|---|---|---|---|
| `custos:client` | Optional | **Audience**: **Included Client Audience** `optio` | Custos requests it. The token of Custos then has the audience `optio`, so that Optio can exchange it. |
| `custos:read` | Optional | **Audience**: **Included Custom Audience** `https://records.example.com/mcp`. **User Attribute** `discord_id` as above. | The read tools of the server. |
| `custos:write` | Optional | The same as `custos:read`. | The upload API and the write tools of the server. |

Create each scope in **Client scopes** with the protocol `OpenID Connect`. Set **Include in token scope** to on for `custos:read` and `custos:write`. Keep the three scopes optional. A default scope goes to each client, and each token then gets its audience.

### The client `optio`

Optio exchanges the token of Custos for a token of the server (RFC 8693). It has no login of its own.

1. In **Clients**, create the client `optio` with the protocol `OpenID Connect`.
2. Set **Client authentication** to on. Set **Standard flow** and **Direct access grants** to off. Set **Standard Token Exchange** to on.
3. Copy the secret from the tab **Credentials**. It is the value of `OPTIO_CLIENT_SECRET` in the file `.env` (step 5).
4. On the tab **Client scopes**, set `custos-discord` and `custos-guild` as **Default**, and `custos:read` and `custos:write` as **Optional**.

An exchanged token gets only the claims of the scopes of the client `optio`. Thus Optio needs `custos-guild` too, or the token of the server has no `active_guild`.

### The MCP endpoint of the server

An LLM harness reads the metadata document of `/mcp`, gets its own client in the realm, and asks the user to log in. A harness gets a client in one of two ways. It sends a registration request, or it uses the URL of its Client ID Metadata Document as the client ID. Keycloak with the feature `cimd` states that it supports the second way, so a harness such as Claude Code uses it.

1. In **Clients**, on the tab **Client registration**, open the anonymous policy **Trusted Hosts**. Add the hosts of the redirect URIs of the harnesses that you permit, for example `localhost` and `127.0.0.1` for a harness on a computer. Set **Host Sending Client Registration Request Must Match** to off, and keep **Client URIs Must Match** on.
2. In **Realm settings**, on the tab **Client policies**, add a profile with the executor `pkce-enforcer` (**Auto-configure** on), and a policy with the condition `any-client` for that profile. Each client then uses PKCE with S256.
3. For a harness that uses a Client ID Metadata Document, such as Claude Code, extend the profile and the policy `custos-cimd`. Without this step, Keycloak shows `Client not found`, because the policy accepts only the host of Optio. In the profile, add to the executor `client-id-metadata-document`:

   ```json
   "cimd-allow-permitted-domains": ["custos.example.com", "oauth2callback", "claude.ai", "localhost", "127.0.0.1"],
   "cimd-resource-indicator-allow-list": ["https://records.example.com/mcp"]
   ```

   `claude.ai` is the host of the client ID. `localhost` and `127.0.0.1` are the hosts of the redirect URIs. Without the allow list of resources, Keycloak refuses the parameter `resource` with `invalid_target`. In the policy, add `claude.ai` to `client-id-uri-allow-permitted-domains` of the condition. For another harness, add the host of its metadata document and the hosts of its redirect URIs.
4. Claude Code requests the scope `offline_access`. A user needs the realm role `offline_access`. Without it, Keycloak answers the token request with `Offline tokens not allowed for the user or client`. A new realm gives this role to each user in its default roles, so this step is for a realm that removed it.

To add the endpoint to a harness, give it the URL of the endpoint as a remote MCP server with the HTTP transport. For example, in Claude Code:

```sh
claude mcp add --transport http custos https://records.example.com/mcp
```

The harness opens the login of Keycloak. The consent page shows the two scopes. A token with `custos:read` only does not list the write tools.

## 3. Prepare the Discord applications

Each service needs its own Discord application. The server registers its commands when it starts, and a registration replaces all the commands of the application. Optio needs a privileged intent that the server does not need.

1. For the server, do the steps of [The Discord application](../server/README.md#the-discord-application).
2. For Optio, do the same steps with a second application. On the page **Bot**, also set **Server Members Intent** to on: Optio reads the roles of the members with an open stream. Keep the other privileged intents off.

Add the bot of Optio to each guild that uses the stream. A stream of a guild without the bot gets one error message.

## 4. Prepare NATS

The broker carries the events of the server to Optio. The operator defines only the broker users and their permissions. Optio makes the stream of the events when it starts ([optio/docs/producers.md](../optio/docs/producers.md#broker)).

1. Copy [optio/nats/nats.conf](../optio/nats/nats.conf) next to the Compose file. It turns on JetStream with the store `/data`, and defines the users `custos` and `optio` with their permissions.
2. Select a password for each user. Use only letters and digits, because the password is part of a URL in the configuration of each service. The NATS server reads the passwords from its environment variables `CUSTOS_PASSWORD` and `OPTIO_PASSWORD` (step 5).

The configuration defines no stream. For each new domain service that publishes events, add a user with the permissions of [optio/docs/producers.md](../optio/docs/producers.md#broker). Optio needs no change.

## 5. Write the Compose file

```yaml
services:
  events:
    image: nats:2.15.3-alpine
    restart: unless-stopped
    command: ["-c", "/etc/nats/nats.conf"]
    environment:
      CUSTOS_PASSWORD: ${CUSTOS_BROKER_PASSWORD}
      OPTIO_PASSWORD: ${OPTIO_BROKER_PASSWORD}
    volumes:
      - ./nats.conf:/etc/nats/nats.conf:ro
      - events-data:/data

  server:
    image: ghcr.io/atlashq-gg/custos-server:0.3.0
    restart: unless-stopped
    environment:
      ConnectionStrings__custos: >-
        Host=postgres;Port=5432;Database=custos;Username=custos;Password=${CUSTOS_DB_PASSWORD}
      ConnectionStrings__events: nats://custos:${CUSTOS_BROKER_PASSWORD}@events:4222
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
    configs:
      - source: server-settings
        target: /app/appsettings.Production.json
    secrets:
      - Discord__Token
    ports:
      - "127.0.0.1:8081:8080"
    networks:
      - default
      - postgres

  optio:
    image: ghcr.io/atlashq-gg/custos-optio:0.1.0
    restart: unless-stopped
    environment:
      ConnectionStrings__events: nats://optio:${OPTIO_BROKER_PASSWORD}@events:4222
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
    configs:
      - source: optio-settings
        target: /app/appsettings.Production.json
    secrets:
      - Optio__OptioClientSecret
      - Optio__DiscordToken
    ports:
      - "127.0.0.1:8080:8080"

configs:
  server-settings:
    content: |
      {
        "Custos": {
          "Authority": "https://id.example.com/realms/custos",
          "Resource": "https://records.example.com/mcp"
        },
        "Mcp": {
          "Enabled": true
        }
      }
  optio-settings:
    content: |
      {
        "Optio": {
          "PublicBaseUrl": "https://custos.example.com",
          "Authority": "https://id.example.com/realms/custos",
          "UploadApiUrl": "http://server:8080/api/v1/uploads"
        }
      }

secrets:
  Discord__Token:
    environment: SERVER_DISCORD_TOKEN
  Optio__OptioClientSecret:
    environment: OPTIO_CLIENT_SECRET
  Optio__DiscordToken:
    environment: OPTIO_DISCORD_TOKEN

volumes:
  events-data:

networks:
  postgres:
    external: true
    name: postgres_default
```

Write the file `.env` next to the Compose file. Do not commit it. Compose reads it for the passwords and the secrets:

```ini
CUSTOS_DB_PASSWORD=change-me
CUSTOS_BROKER_PASSWORD=changeMe1
OPTIO_BROKER_PASSWORD=changeMe2
SERVER_DISCORD_TOKEN=<bot token of the server>
OPTIO_DISCORD_TOKEN=<bot token of Optio>
OPTIO_CLIENT_SECRET=<secret of the client optio>
```

- `image`: a release tag (`X.Y.Z`) of each service. The tags above are examples. `unstable` changes with each push to `main` ([optio/README.md](../optio/README.md#container), [server/README.md](../server/README.md#container)).
- `events`: the NATS server. Its service name is the host in the connection strings. The volume `events-data` keeps the events of the replay window over a restart of the broker. The broker needs no public route.
- `server-settings` and `optio-settings`: the settings of each service as JSON, in the sections `Custos` and `Mcp` of the server ([server/README.md](../server/README.md#configuration)) and `Optio` of Optio ([optio/README.md](../optio/README.md#configuration)). Compose mounts each content as `/app/appsettings.Production.json` of its service, and the service reads it when it starts. Compose replaces `${...}` in the content, so do not put a `$` in a value.
- `PublicBaseUrl`: the origin that Custos and Keycloak use to reach Optio. The client ID of Custos is `{PublicBaseUrl}/.well-known/custos-client`.
- `Authority`: the issuer of the realm, exactly as the `iss` claim of a token has it. Each service gets the metadata and the signing keys of Keycloak from this URL, so each container must reach it.
- `Resource` of the server: the public URL of the MCP endpoint. It is the audience of each token of the server, also of a token that Optio gets by exchange. Thus it is the public URL even when Optio calls the server on the internal network.
- `UploadApiUrl`: the internal URL of the upload API of the server. The upload API needs no public route.
- `secrets`: Compose gets each secret from a variable of the file `.env` next to the Compose file, and mounts it as `/run/secrets/<name>`. Each service reads each file of `/run/secrets` as a configuration key, and `__` in a file name is `:`. Without `Discord__Token`, the server has no Discord connection. Without `Optio__DiscordToken`, Optio has no stream.
- `ConnectionStrings__*`: the passwords come from the file `.env` too. Without `ConnectionStrings__events`, the server publishes no events, and Optio has no stream. A service starts without the broker, and connects again by itself.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: each service applies `X-Forwarded-Proto` and `X-Forwarded-For` of the proxy. A service then trusts these headers from each client that reaches its port, so only the proxy must reach it.
- Telemetry: add `OTEL_EXPORTER_OTLP_ENDPOINT` to the environment of a service to export its traces, metrics, and logs. Without it, a service exports nothing. The service names are `Custos` for the server and `Optio`. `OTEL_SERVICE_NAME` replaces a name.
- `networks`: an existing network that PostgreSQL is on. `docker network ls` shows its name. If PostgreSQL runs on another host, remove that network, and use the host name in `Host=`.
- `ports`: the reverse proxy on the host connects to `127.0.0.1:8080` for Optio and `127.0.0.1:8081` for the server. If the proxy runs in Docker, put the services on the network of the proxy instead.

The value `UploadMaxBytes` must be equal in the two services. The value `UploadMinIntervalSeconds` too.

## 6. Configure the reverse proxy

| Public host | Paths | Target |
|---|---|---|
| `custos.example.com` | All | Optio, port 8080 |
| `records.example.com` | `/mcp` and `/.well-known/oauth-protected-resource` | The server, port 8080 |

- Keep the `Host` header of the request, and send `X-Forwarded-Proto` and `X-Forwarded-For`.
- Keycloak gets `/.well-known/custos-client` through the proxy, so the certificate must be valid for Keycloak.
- The stream at `/api/v1/stream` is a long response with Server-Sent Events. Turn off the buffer of the response for this path, and let a response run for longer than the lifetime of an access token. Optio sends a comment 15 seconds after its last write, and closes the stream when the token expires.
- Give no public route to the other paths of the server, and no route to the broker.

## 7. Start and check the system

```sh
docker compose up -d
docker compose logs -f optio server
curl https://custos.example.com/health/ready
curl https://custos.example.com/.well-known/custos-configuration
curl https://records.example.com/.well-known/oauth-protected-resource
```

1. `/health/ready` of each service must give `Healthy`. The server checks its database. Readiness does not depend on the broker, because the upload works without it.
2. The log of Optio must show `The stream OPTIO of the events is ready`. A warning that the event broker is not available shows a wrong host, user, or password.
3. The discovery document must show the public URL in `upload_endpoint` and `client_id`. With the broker and the Discord token of Optio, it has `stream_endpoint`.
4. The metadata document of the server must show `"resource": "https://records.example.com/mcp"`.
5. Set the URL of Optio in Custos, and log in. Keycloak shows a consent page on the first login.
6. After an upload that changes a stockpile, the log of the server shows no warning of the event broker. A member with a read role of the guild gets the event on the stream.
7. With the Discord tokens, the logs have no warning about a token. In a guild, `/custosadmin settings view` works.

## Operation

- Run one instance of each service. The rate limits, the cache of the war API, the Discord gateways, and the count of open streams are in the memory of the process.
- Each image of a service has a `HEALTHCHECK` with the flag `--healthcheck`, because the images have no shell and no `curl`. The check uses `/health/alive`. Check `/health/ready` from the proxy or from a monitor.
- Back up the database of the server before you update an image. The server applies new migrations when it starts.
- The broker keeps the events of the replay window, 15 minutes by default, and removes each event at its age. Optio sets the age limit of the stream at each start, from `ReplayWindowMinutes`. Optio keeps no events. After a restart of Optio, a client that connects again with its position gets the events that it missed.
- When the broker is not available, an upload still succeeds, and its event can be lost. A stream request gets `503`, and Custos tries again later.
- The archive and the purge of a war are manual ([server/README.md](../server/README.md#wars)).
