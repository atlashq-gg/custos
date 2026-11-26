# Keycloak setup

This document sets up each part of a Keycloak realm that Custos, Optio, and the Server need. Do the steps in the admin console of Keycloak. The realm can also hold the clients of other services.

The [development realm](custos-realm.json) holds the same settings, with the values of development. The tests and the AppHost import it. When this document and the development realm disagree, the development realm is correct, and this document needs a fix.

## Values

Replace these example values with your own:

| Value | Example |
|---|---|
| Host of Optio | `custos.example.com` |
| Public URL of the MCP endpoint of the Server (`Custos:Resource`) | `https://records.example.com/mcp` |
| Issuer of the realm (`Authority`) | `https://id.example.com/realms/custos` |
| ID of the Discord guild of the users | `123456789012345678` |

## Parts

| Step | Part | Use |
|---|---|---|
| [1](#1-start-keycloak) | Features of Keycloak | The Client ID Metadata Document (CIMD) of Custos, and the parameter `resource` of a harness. |
| [2](#2-realm-settings) | Realm settings | The lifetime of a login of Custos. |
| [3](#3-user-attribute-discord_id) | User attribute `discord_id` | The Discord user ID of each user. |
| [4](#4-client-scopes) | Client scopes | The claims and the audiences of each token. |
| [5](#5-client-optio) | Client `optio` | The token exchange of Optio. |
| [6](#6-client-policies) | Client policies | The registration of Custos and of a harness through a CIMD, and PKCE. |
| [7](#7-client-registration) | Client registration | The registration of a harness through a registration request. |
| [8](#8-users) | Users | The values of each user. |
| [9](#9-other-services-of-optio) | Other services of Optio | The scopes of each other MCP service of Optio. |
| [10](#10-check) | Check | The tokens that each part gives. |

## 1. Start Keycloak

Use Keycloak 26.8 or later.

1. Add the features `cimd` and `resource-indicators` to the option `--features`, or to the environment variable `KC_FEATURES`. Keep the other features that you already use in the same list. For example: `KC_FEATURES=cimd,resource-indicators`.
2. Restart Keycloak.
3. Make sure that the log of Keycloak shows `Experimental features enabled: cimd:v1, resource-indicators:v1`.

Keycloak 26.8 has the standard token exchange on by default. It needs no feature.

## 2. Realm settings

Select the realm of Custos, or create one. Its issuer is the value of `Authority` in the settings of each service.

Custos does not request `offline_access`, so the SSO session sets how long a login of Custos lasts. In **Realm settings**, on the tab **Sessions**:

| Field | Value |
|---|---|
| **SSO Session Idle** | 7 days |
| **SSO Session Max** | 30 days |

A shorter value makes the user of Custos log in again sooner. These settings apply to each client of the realm.

## 3. User attribute `discord_id`

Optio and the Server need the Discord user ID of the user in the claim `discord_id` of each access token. Keycloak removes a user attribute that the user profile does not declare. Thus declare the attribute first.

In **Realm settings**, on the tab **User profile**, select **Create attribute**:

| Field | Value |
|---|---|
| **Attribute [Name]** | `discord_id` |
| **Display name** | `Discord user ID` |
| **Multivalued** | Off |
| **Attribute group** | None |
| **Enabled when** | Always |
| **Required field** | Off |
| **Who can edit?** | admin |
| **Who can view?** | admin, user |

Then add a validator. Select **Add validator**:

| Field | Value |
|---|---|
| **Validator type** | `pattern` |
| **RegExp pattern** | `^[0-9]{17,20}$` |
| **Error message key** | `A Discord user ID has 17 to 20 digits.` |

Only an administrator can set the value, so a user cannot claim the Discord account of another user.

## 4. Client scopes

Create each scope in **Client scopes** with **Create client scope**. Use the **Protocol** `OpenID Connect` and the **Type** `None` for each. Step [4.6](#46-assigned-types) sets the assigned types after all scopes exist.

Then add the mappers of the scope. On the tab **Mappers** of the scope, select **Configure a new mapper** for the first mapper, and **Add mapper** → **By configuration** for each other mapper. A field of a mapper that a table does not show keeps its default value.

### 4.1 `custos-discord`

The Discord user ID in each token of each client.

| Field | Value |
|---|---|
| **Name** | `custos-discord` |
| **Description** | `The Discord user ID of the user in the claim discord_id.` |
| **Display on consent screen** | Off |
| **Include in token scope** | Off |

Mapper of the type **User Attribute**:

| Field | Value |
|---|---|
| **Name** | `discord_id` |
| **User Attribute** | `discord_id` |
| **Token Claim Name** | `discord_id` |
| **Claim JSON Type** | `String` |
| **Add to ID token** | Off |
| **Add to access token** | On |
| **Add to userinfo** | Off |
| **Add to token introspection** | On |

### 4.2 `custos-guild`

The Discord guild of the user in the claim `active_guild`. Outside the environment `Development`, Optio and the Server refuse a token with no `active_guild` with `403`. The authenticator that lets a user choose a guild does not exist yet ([#1](https://github.com/atlashq-gg/custos/issues/1)). Until it does, this scope gives each token of the realm the same guild. The development realm has no such scope: development uses `DevelopmentGuildId` instead.

| Field | Value |
|---|---|
| **Name** | `custos-guild` |
| **Description** | `The guild of the user in the claim active_guild.` |
| **Display on consent screen** | Off |
| **Include in token scope** | Off |

Mapper of the type **Hardcoded claim**:

| Field | Value |
|---|---|
| **Name** | `active_guild` |
| **Token Claim Name** | `active_guild` |
| **Claim value** | The guild ID, for example `123456789012345678` |
| **Claim JSON Type** | `String` |
| **Add to ID token** | Off |
| **Add to access token** | On |
| **Add to userinfo** | Off |
| **Add to token introspection** | On |

When the authenticator exists, remove this scope.

### 4.3 `custos:client`

Custos requests this scope. The token of Custos then has the audience `optio`, so that Optio accepts it and can exchange it. Create the client `optio` ([step 5](#5-client-optio)) before this mapper, because the mapper selects the client.

| Field | Value |
|---|---|
| **Name** | `custos:client` |
| **Description** | `Adds the audience optio to the token of Custos, so that Optio can exchange it.` |
| **Display on consent screen** | Off |
| **Include in token scope** | On |

Mapper of the type **Audience**:

| Field | Value |
|---|---|
| **Name** | `audience` |
| **Included Client Audience** | `optio` |
| **Included Custom Audience** | (empty) |
| **Add to ID token** | Off |
| **Add to access token** | On |
| **Add to token introspection** | On |

### 4.4 `custos:read`

The read tools of the Server. Optio gets a token with this scope by exchange. A harness requests it for the MCP endpoint of the Server.

| Field | Value |
|---|---|
| **Name** | `custos:read` |
| **Description** | `Read the guild records through the MCP endpoint of Custos.` |
| **Display on consent screen** | On |
| **Consent screen text** | `Read your guild records in Custos` |
| **Include in token scope** | On |

Two mappers. A mapper of the type **Audience**:

| Field | Value |
|---|---|
| **Name** | `audience` |
| **Included Client Audience** | (empty) |
| **Included Custom Audience** | The public URL of the MCP endpoint, for example `https://records.example.com/mcp` |
| **Add to ID token** | Off |
| **Add to access token** | On |
| **Add to token introspection** | On |

A mapper of the type **User Attribute**, with the values of the mapper of [4.1](#41-custos-discord). A client that does not have `custos-discord` then gets the claim too.

### 4.5 `custos:write`

The upload API and the write tools of the Server. The settings are the same as those of `custos:read`, except these:

| Field | Value |
|---|---|
| **Name** | `custos:write` |
| **Description** | `Change structure labels through the MCP endpoint of Custos.` |
| **Consent screen text** | `Change structure labels in Custos` |

Add the same two mappers as for `custos:read`.

### 4.6 Assigned types

In the list **Client scopes**, set the **Assigned type** of each scope:

| Scope | Assigned type |
|---|---|
| `custos-discord` | Default |
| `custos-guild` | Default |
| `custos:client` | Optional |
| `custos:read` | Optional |
| `custos:write` | Optional |

The assigned type applies to each new client, also to a client that Keycloak registers from a CIMD. A client that exists already keeps its scopes. Add a scope to such a client in **Clients**, on its tab **Client scopes**.

Keep `custos:client`, `custos:read`, and `custos:write` optional. A default scope goes to each token of each client, and each token would then get its audience.

## 5. Client `optio`

Optio exchanges the token of Custos for a token of the Server (RFC 8693). It has no login of its own.

In **Clients**, select **Create client**. On the page **General settings**:

| Field | Value |
|---|---|
| **Client type** | `OpenID Connect` |
| **Client ID** | `optio` |
| **Name** | `Optio` |
| **Description** | `Exchanges the token of Custos for a token of a domain service.` |

On the page **Capability config**:

| Field | Value |
|---|---|
| **Client authentication** | On |
| **Authorization** | Off |
| **Standard flow** | Off |
| **Direct access grants** | Off |
| **Implicit flow** | Off |
| **Service accounts roles** | Off |
| **Standard Token Exchange** | On |
| **OAuth 2.0 Device Authorization Grant** | Off |
| **OIDC CIBA Grant** | Off |

On the page **Login settings**, leave each field empty. Then select **Save**.

On the tab **Settings** of the client, make sure that **Consent required** is off. With consent, Keycloak refuses the exchange with `Missing consents for Token Exchange`.

On the tab **Credentials**, keep the **Client Authenticator** `Client Id and Secret`. Copy the **Client Secret** to `OPTIO_CLIENT_SECRET` in the file `.env` of the deployment ([../README.md](../README.md#5-write-the-compose-file)).

On the tab **Client scopes**, set these scopes. Use **Add client scope** for a scope that is not in the list, and remove each other scope that the client does not need:

| Scope | Assigned type |
|---|---|
| `basic` | Default |
| `custos-discord` | Default |
| `custos-guild` | Default |
| `custos:read` | Optional |
| `custos:write` | Optional |

An exchanged token gets only the claims of the scopes of the client `optio`. Thus `optio` needs `custos-discord` and `custos-guild`, or the token of the Server has no `discord_id` and no `active_guild`. Do not give `optio` a scope of another service as **Default**. A default scope adds its audience to each exchanged token, also to the token of the Server.

## 6. Client policies

Keycloak registers Custos from the CIMD that Optio serves, at `https://custos.example.com/.well-known/custos-client`. A harness such as Claude Code also uses a CIMD for the MCP endpoint of the Server. A client profile accepts these documents, and a client policy applies the profile to a client ID on a trusted host.

The files [profiles.json](profiles.json) and [policies.json](policies.json) hold the profiles and the policies of Custos:

| Name | Kind | Content |
|---|---|---|
| `custos-cimd` | Profile | The executor `client-id-metadata-document`: the trusted hosts, and the resource of the Server. |
| `custos-pkce` | Profile | The executor `pkce-enforcer`: PKCE with S256. |
| `custos-cimd` | Policy | The condition `client-id-uri`: a client ID on the host of Optio or of a harness gets the profile `custos-cimd`. |
| `custos-pkce` | Policy | The condition `any-client`: each client gets the profile `custos-pkce`. |

In the files, replace `custos.example.com` and `https://records.example.com/mcp` with your values. Then add or remove the hosts of the harnesses:

| Host | Profile, **Trusted domains** | Policy, **Trusted domains** | Why |
|---|---|---|---|
| The host of Optio | Yes | Yes | The client ID of Custos. |
| `oauth2callback` | Yes | No | The redirect URI `custos://oauth2callback` of Custos. Keycloak reads its first segment as the host. |
| `claude.ai` | Yes | Yes | The client ID of Claude Code. |
| `localhost`, `127.0.0.1` | Yes | No | The redirect URIs of a harness on a computer, such as Claude Code. |

Without the host of a client ID in the policy, Keycloak shows `Client not found`. Without the resource of the Server in **Resource indicator allow list**, Keycloak refuses the parameter `resource` of a harness with `invalid_target`.

CAUTION: The policy `custos-pkce` applies to each client of the realm, also to the clients of another service. Before you add it to a shared realm, make sure that each client of the realm can use PKCE with S256.

CAUTION: The command `kcadm.sh update` replaces all profiles or all policies of the realm. Get the current lists first, and add the entries of Custos to them:

```sh
kcadm.sh config credentials --server https://id.example.com --realm master --user admin
kcadm.sh get realms/custos/client-policies/profiles > profiles.current.json
kcadm.sh get realms/custos/client-policies/policies > policies.current.json
# Add the entries of profiles.json and policies.json to the two current files, then:
kcadm.sh update realms/custos/client-policies/profiles -f profiles.current.json
kcadm.sh update realms/custos/client-policies/policies -f policies.current.json
```

The admin console has the same settings in **Realm settings**, on the tab **Client policies**. There, **Allow http scheme** of the executor must stay off in production.

## 7. Client registration

A harness that does not use a CIMD sends a registration request. In **Clients**, on the tab **Client registration**, open the anonymous policy **Trusted Hosts**:

| Field | Value |
|---|---|
| **Trusted Hosts** | The hosts of the redirect URIs of the harnesses that you permit, for example `localhost` and `127.0.0.1` |
| **Host Sending Client Registration Request Must Match** | Off |
| **Client URIs Must Match** | On |

Keep the other anonymous policies of Keycloak.

## 8. Users

For each user of Custos:

1. Set the attribute `discord_id` to the Discord user ID of the user ([step 3](#3-user-attribute-discord_id)).
2. Make sure that the user has the realm role `offline_access`, for a harness such as Claude Code. Claude Code requests the scope `offline_access`. Without the role, Keycloak refuses its token request with `Offline tokens not allowed for the user or client`. A new realm gives this role to each user through its default roles.

## 9. Other services of Optio

Optio exchanges the token of Custos for a token of each MCP service in `McpServices` of its configuration. For each other service:

1. Make sure that the read scope and the write scope of the service exist in the realm, each with an **Audience** mapper for the resource of that service.
2. Make sure that each of the two scopes puts `discord_id` in the access token, if the service needs it.
3. On the tab **Client scopes** of the client `optio`, add the two scopes as **Optional**. If a scope is **Default** there, change it to **Optional**.
4. Set `ReadScope` and `WriteScope` of the service in `McpServices` to the names of the two scopes.

## 10. Check

Keycloak shows the token of a client for a user without a login. In **Clients**, open the client, then the tab **Client scopes**, then the sub-tab **Evaluate**. Select a user, enter the scopes, and open **Generated access token**.

| Client | Scopes | The access token must have |
|---|---|---|
| `optio` | `custos:read` | `aud` with the URL of the MCP endpoint, `azp` `optio`, `scope` with `custos:read`, `discord_id`, `active_guild` |
| `optio` | `custos:write` | The same, with `custos:write` |
| The client of Custos | `custos:client` | `aud` with `optio`, `azp` with the URL of the CIMD of Custos, `discord_id`, `active_guild`, `sid` |

Keycloak registers the client of Custos at the first login of Custos. Before that login, check only `optio`.

Then check the system:

1. Log in with Custos. Keycloak shows a consent page at the first login.
2. Send a query in Custos, or an upload. The log of Optio must show no warning that a service is not available, and no refused token exchange.

## Update

A change of the Keycloak settings comes with a release of Optio or of the Server:

- The `CHANGELOG.md` of the project has a line that starts with `Keycloak:`. It names the step of this document that changed.
- This document and the [development realm](custos-realm.json) change together.

Before you deploy a new release, read the `Keycloak:` lines of each `CHANGELOG.md` since the deployed version. Do each changed step again, then do the checks of [step 10](#10-check).
