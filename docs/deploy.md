# Deploy

This guide deploys the services of the system with Docker Compose: `optio`, the server, and the RabbitMQ broker. It uses an existing PostgreSQL and an existing Keycloak. Custos calls only `optio` ([ADR 0006](adr/0006-optio-is-the-service-of-the-contract.md)). `optio` calls the server with a token of the user, and reads the events of the server from the broker ([ADR 0007](adr/0007-a-broker-carries-domain-events.md)).

The options of each service are in [optio/README.md](../optio/README.md#configuration) and [server/README.md](../server/README.md#configuration). The examples use these values. Replace them with your own.

| Value | Example |
|---|---|
| Public URL of `optio`, which Custos uses | `https://custos.example.com` |
| Public URL of the MCP endpoint of the server | `https://records.example.com/mcp` |
| Issuer of the Keycloak realm | `https://id.example.com/realms/custos` |
| Databases and roles | `custos` for the server, `optio` for `optio` |

The system needs:

- PostgreSQL 18. The tests and the AppHost use 18 only. The server uses list partitions and advisory locks.
- Keycloak 26.8 or later, with the features `cimd` and `resource-indicators`.
- RabbitMQ 4 with stream queues. The AppHost uses the image `rabbitmq:4.3-alpine`.
- A reverse proxy that terminates TLS. Each container serves HTTP on port 8080.
- Two Discord applications, each with a bot: one for the server, and one for `optio`.
- For the query: a key of the model provider and a system prompt. Without them, `optio` serves no query, and the upload and the stream still work.

## 1. Prepare PostgreSQL

Create a role and a database for each service. Each service applies its migrations when it starts, so each role must be able to create tables in its database.

```sql
CREATE ROLE custos LOGIN PASSWORD 'change-me';
CREATE DATABASE custos OWNER custos;
CREATE ROLE optio LOGIN PASSWORD 'change-me-too';
CREATE DATABASE optio OWNER optio;
```

The roles need no other privilege. Make sure that the containers can reach PostgreSQL, for example through a shared Docker network (step 5).

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

Keycloak gets the registration of Custos from the Client ID Metadata Document of `optio`, at `https://custos.example.com/.well-known/custos-client`. The profile accepts that document. The policy applies the profile to a client ID on the host of `optio`. Put the host of `optio` in both files. The domain `oauth2callback` permits the redirect URI `custos://oauth2callback`.

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
      "description": "Clients whose ID is the URL of the metadata document on optio.",
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

The server and `optio` need the Discord user ID in the claim `discord_id` of the access token. The development realm shows one method: a user attribute and a default client scope.

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
| `custos:client` | Optional | **Audience**: **Included Client Audience** `optio` | Custos requests it. The token of Custos then has the audience `optio`, so that `optio` can exchange it. |
| `custos:read` | Optional | **Audience**: **Included Custom Audience** `https://records.example.com/mcp`. **User Attribute** `discord_id` as above. | The read tools of the server. |
| `custos:write` | Optional | The same as `custos:read`. | The upload API and the write tools of the server. |

Create each scope in **Client scopes** with the protocol `OpenID Connect`. Set **Include in token scope** to on for `custos:read` and `custos:write`. Keep the three scopes optional. A default scope goes to each client, and each token then gets its audience.

### The client `optio`

`optio` exchanges the token of Custos for a token of the server (RFC 8693). It has no login of its own.

1. In **Clients**, create the client `optio` with the protocol `OpenID Connect`.
2. Set **Client authentication** to on. Set **Standard flow** and **Direct access grants** to off. Set **Standard Token Exchange** to on.
3. Copy the secret from the tab **Credentials**. It is the value of `OPTIO_CLIENT_SECRET` in the file `.env` (step 5).
4. On the tab **Client scopes**, set `custos-discord` and `custos-guild` as **Default**, and `custos:read` and `custos:write` as **Optional**.

An exchanged token gets only the claims of the scopes of the client `optio`. Thus `optio` needs `custos-guild` too, or the token of the server has no `active_guild`.

For each other domain service of the realm, give the client `optio` its scopes as optional scopes ([optio/README.md](../optio/README.md#add-a-service)).

### The MCP endpoint of the server

An LLM harness reads the metadata document of `/mcp`, gets its own client in the realm, and asks the user to log in. A harness gets a client in one of two ways. It sends a registration request, or it uses the URL of its Client ID Metadata Document as the client ID. Keycloak with the feature `cimd` states that it supports the second way, so a harness such as Claude Code uses it.

1. In **Clients**, on the tab **Client registration**, open the anonymous policy **Trusted Hosts**. Add the hosts of the redirect URIs of the harnesses that you permit, for example `localhost` and `127.0.0.1` for a harness on a computer. Set **Host Sending Client Registration Request Must Match** to off, and keep **Client URIs Must Match** on.
2. In **Realm settings**, on the tab **Client policies**, add a profile with the executor `pkce-enforcer` (**Auto-configure** on), and a policy with the condition `any-client` for that profile. Each client then uses PKCE with S256.
3. For a harness that uses a Client ID Metadata Document, such as Claude Code, extend the profile and the policy `custos-cimd`. Without this step, Keycloak shows `Client not found`, because the policy accepts only the host of `optio`. In the profile, add to the executor `client-id-metadata-document`:

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

Each service needs its own Discord application. Each service registers its commands when it starts, and a registration replaces all the commands of the application.

1. For the server, do the steps of [The Discord application](../server/README.md#the-discord-application).
2. For `optio`, do the same steps with a second application. On the page **Bot**, also set **Server Members Intent** to on: `optio` reads the roles of the members with an open stream. Keep the other privileged intents off.

The server registers `/custosadmin` and `/custos`. `optio` registers `/optioadmin` ([optio/README.md](../optio/README.md#prompts)).

## 4. Prepare RabbitMQ

The broker carries the events of the server to `optio`. The operator makes each object of the broker. A service declares nothing ([optio/docs/producers.md](../optio/docs/producers.md#broker)).

1. Copy [optio/rabbitmq/definitions.json](../optio/rabbitmq/definitions.json) next to the Compose file. It holds the exchange `events.custos`, the stream `events.custos.stream`, the binding, and the users `custos` and `optio` with their permissions.
2. Make a password hash for each user, and put it in `password_hash` of the user in the copy. The hashes in the repository are for development only.

   ```sh
   docker run --rm rabbitmq:4.3-alpine rabbitmqctl hash_password '<password of the user>'
   ```

   The last line of the output is the hash.

3. Write the file `rabbitmq.conf` next to the Compose file:

   ```ini
   load_definitions = /etc/rabbitmq/definitions.json
   ```

With `load_definitions`, RabbitMQ makes no default user `guest`. For an administrator, add a user with the tag `administrator` to the copy.

## 5. Write the Compose file

```yaml
services:
  broker:
    image: rabbitmq:4.3-alpine
    restart: unless-stopped
    volumes:
      - ./rabbitmq.conf:/etc/rabbitmq/conf.d/20-definitions.conf:ro
      - ./definitions.json:/etc/rabbitmq/definitions.json:ro
      - broker-data:/var/lib/rabbitmq

  server:
    image: ghcr.io/atlashq-gg/custos-server:0.3.0
    restart: unless-stopped
    environment:
      ConnectionStrings__custos: >-
        Host=postgres;Port=5432;Database=custos;Username=custos;Password=${CUSTOS_DB_PASSWORD}
      ConnectionStrings__events: amqp://custos:${CUSTOS_BROKER_PASSWORD}@broker:5672
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      OTEL_SERVICE_NAME: custos-server
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
      ConnectionStrings__optio: >-
        Host=postgres;Port=5432;Database=optio;Username=optio;Password=${OPTIO_DB_PASSWORD}
      ConnectionStrings__events: amqp://optio:${OPTIO_BROKER_PASSWORD}@broker:5672
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      OTEL_SERVICE_NAME: custos-optio
    configs:
      - source: optio-settings
        target: /app/appsettings.Production.json
      - source: optio-system-prompt
        target: /run/secrets/Optio__SystemPrompt
    secrets:
      - Optio__OptioClientSecret
      - Optio__DiscordToken
      - Optio__ModelApiKey
    ports:
      - "127.0.0.1:8080:8080"
    networks:
      - default
      - postgres

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
  optio-system-prompt:
    file: ./system-prompt.txt
  optio-settings:
    content: |
      {
        "Optio": {
          "PublicBaseUrl": "https://custos.example.com",
          "Authority": "https://id.example.com/realms/custos",
          "UploadApiUrl": "http://server:8080/api/v1/uploads",
          "McpServices": [
            {
              "Name": "custos",
              "Url": "http://server:8080/mcp",
              "ReadScope": "custos:read",
              "WriteScope": "custos:write"
            }
          ]
        }
      }

secrets:
  Discord__Token:
    environment: SERVER_DISCORD_TOKEN
  Optio__OptioClientSecret:
    environment: OPTIO_CLIENT_SECRET
  Optio__DiscordToken:
    environment: OPTIO_DISCORD_TOKEN
  Optio__ModelApiKey:
    environment: OPTIO_MODEL_KEY

volumes:
  broker-data:

networks:
  postgres:
    external: true
    name: postgres_default
```

Write the file `.env` next to the Compose file. Do not commit it. Compose reads it for the passwords and the secrets:

```ini
CUSTOS_DB_PASSWORD=change-me
CUSTOS_BROKER_PASSWORD=change-me
OPTIO_DB_PASSWORD=change-me-too
OPTIO_BROKER_PASSWORD=change-me-too
SERVER_DISCORD_TOKEN=<bot token of the server>
OPTIO_DISCORD_TOKEN=<bot token of optio>
OPTIO_CLIENT_SECRET=<secret of the client optio>
OPTIO_MODEL_KEY=<key of the model provider>
```

- `image`: a release tag (`X.Y.Z`) of each service. The tags above are examples. `unstable` changes with each push to `main` ([optio/README.md](../optio/README.md#container), [server/README.md](../server/README.md#container)).
- `server-settings` and `optio-settings`: the settings of each service as JSON, in the sections `Custos` and `Mcp` of the server ([server/README.md](../server/README.md#configuration)) and `Optio` of `optio` ([optio/README.md](../optio/README.md#configuration)). Compose mounts each content as `/app/appsettings.Production.json` of its service, and the service reads it when it starts. Compose replaces `${...}` in the content, so do not put a `$` in a value.
- `PublicBaseUrl`: the origin that Custos and Keycloak use to reach `optio`. The client ID of Custos is `{PublicBaseUrl}/.well-known/custos-client`.
- `Authority`: the issuer of the realm, exactly as the `iss` claim of a token has it. Each service gets the metadata and the signing keys of Keycloak from this URL, so each container must reach it.
- `Resource` of the server: the public URL of the MCP endpoint. It is the audience of each token of the server, also of a token that `optio` gets by exchange. Thus it is the public URL even when `optio` calls the server on the internal network.
- `UploadApiUrl` and the `Url` of the MCP service: the internal URLs of the server. The upload API needs no public route. Add an entry to `McpServices` for each other domain service ([optio/README.md](../optio/README.md#add-a-service)).
- `secrets`: Compose gets each secret from a variable of the file `.env` next to the Compose file, and mounts it as `/run/secrets/<name>`. Each service reads each file of `/run/secrets` as a configuration key, and `__` in a file name is `:`. Without `Discord__Token`, the server has no Discord connection. Without `Optio__DiscordToken`, `optio` has no stream. Without `Optio__ModelApiKey`, `optio` serves no query.
- `optio-system-prompt`: the system prompt of the query, in the file `system-prompt.txt`. It is not a secret, but a file is the easier way to give a value with many lines. Compose mounts it as `/run/secrets/Optio__SystemPrompt`, so that `optio` reads it as the key `Optio:SystemPrompt`. Without it, `optio` serves no query. The repository holds an example: [optio/docs/system-prompt.example.txt](../optio/docs/system-prompt.example.txt).
- `ConnectionStrings__*`: the passwords come from the file `.env` too.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: each service applies `X-Forwarded-Proto` and `X-Forwarded-For` of the proxy. A service then trusts these headers from each client that reaches its port, so only the proxy must reach it.
- `OTEL_*`: the telemetry. Add `OTEL_EXPORTER_OTLP_ENDPOINT` to export it. Without it, a service exports nothing.
- `networks`: an existing network that PostgreSQL is on. `docker network ls` shows its name. If PostgreSQL runs on another host, remove that network, and use the host name in `Host=`.
- `ports`: the reverse proxy on the host connects to `127.0.0.1:8080` for `optio` and `127.0.0.1:8081` for the server. If the proxy runs in Docker, put the services on the network of the proxy instead.

The value `UploadMaxBytes` must be equal in the two services. The value `UploadMinIntervalSeconds` too.

## 6. Configure the reverse proxy

| Public host | Paths | Target |
|---|---|---|
| `custos.example.com` | All | `optio`, port 8080 |
| `records.example.com` | `/mcp` and `/.well-known/oauth-protected-resource` | The server, port 8080 |

- Keep the `Host` header of the request, and send `X-Forwarded-Proto` and `X-Forwarded-For`.
- Keycloak gets `/.well-known/custos-client` through the proxy, so the certificate must be valid for Keycloak.
- The stream at `/api/v1/stream` is a long response with Server-Sent Events. Turn off the buffer of the response for this path, and let a response run for longer than the lifetime of an access token. `optio` sends a comment each 15 seconds, and closes the stream when the token expires.
- Give no public route to the other paths of the server.

## 7. Start and check the system

```sh
docker compose up -d
docker compose logs -f optio server
curl https://custos.example.com/health/ready
curl https://custos.example.com/.well-known/custos-configuration
curl https://records.example.com/.well-known/oauth-protected-resource
```

1. `/health/ready` of each service must give `Healthy`. It checks the database of the service.
2. The discovery document must show the public URL in `upload_endpoint` and `client_id`. With the Discord token of `optio`, it has `stream_endpoint`. With the model key and the system prompt, it has `query_endpoint`.
3. The metadata document of the server must show `"resource": "https://records.example.com/mcp"`.
4. Set the URL of `optio` in Custos, and log in. Keycloak shows a consent page on the first login.
5. After an upload, the table `uploads` of the database `custos` has one row, and the log of the server shows no error of the broker.
6. With the Discord tokens, the logs have no warning about a token. In a guild, `/custosadmin settings view` and `/optioadmin settings` work.

## Operation

- Run one instance of each service. The rate limits, the cache of the war API, the Discord gateway, the replay window of the stream, and the open confirmations are in the memory of the process.
- Each image has a `HEALTHCHECK` with the flag `--healthcheck`, because the images have no shell and no `curl`. The check uses `/health/alive`. Check `/health/ready` from the proxy or from a monitor.
- Back up the databases before you update an image. Each service applies new migrations when it starts.
- The broker keeps the events of the last hour. After a restart, `optio` reads the events of its replay window again from the broker.
- The archive and the purge of a war are manual ([server/README.md](../server/README.md#wars)).
