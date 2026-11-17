# Discovery

The user gives Custos one URL, the service URL. Custos gets all other configuration from the discovery document of that service.

## Location

Custos makes the discovery URL from the service URL. It inserts `/.well-known/custos-configuration` between the origin and the path, as in [RFC 8414 section 3.1](https://www.rfc-editor.org/rfc/rfc8414#section-3.1).

| Service URL | Discovery URL |
|---|---|
| `https://example.com` | `https://example.com/.well-known/custos-configuration` |
| `https://example.com/` | `https://example.com/.well-known/custos-configuration` |
| `https://example.com/regiment/42` | `https://example.com/.well-known/custos-configuration/regiment/42` |

- The service URL MUST NOT have a query or a fragment.
- Custos sends `GET` to the discovery URL without credentials.
- The service MUST answer `200` with `Content-Type: application/json`.
- Custos MUST reject a discovery URL with the scheme `http`, unless the host is `localhost` or a loopback address.

## Document

The schema is [schemas/discovery.schema.json](schemas/discovery.schema.json).

| Member | Type | Required | Content |
|---|---|---|---|
| `custos_versions_supported` | array of strings | yes | The contract versions that the service supports ([04-versioning.md](04-versioning.md)). |
| `service_name` | string | yes | A short name that Custos shows to the user. |
| `service_documentation` | string (URL) | no | A page for humans about the service. |
| `upload_endpoint` | string (URL) | yes | The endpoint for the upload ([03-upload.md](03-upload.md)). |
| `upload_max_bytes` | integer | yes | The largest body that the upload endpoint accepts. |
| `upload_min_interval_seconds` | integer | yes | The shortest time between two uploads of one client. |
| `openid_configuration` | string (URL) | yes | The URL of the metadata document of the authorization server ([Authorization server](#authorization-server)). |
| `client_id` | string | yes | The client ID that Custos uses at the authorization server. |
| `scopes` | array of strings | yes | The scopes that Custos requests. It can be empty. |

Each URL in the document MUST use HTTPS, with the same exception for loopback hosts. The URLs can point to a different host than the service URL, for example to an identity provider.

## Authorization server

`openid_configuration` links to the metadata document of the authorization server, for example `https://id.example.com/realms/custos/.well-known/openid-configuration`. The format is [OpenID Connect Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html) or [RFC 8414](https://www.rfc-editor.org/rfc/rfc8414). Custos gets it after the discovery document, without credentials.

Custos uses these members. It ignores all other members.

| Member | Required | Content |
|---|---|---|
| `authorization_endpoint` | yes | The authorization endpoint ([02-authentication.md](02-authentication.md)). |
| `token_endpoint` | yes | The token endpoint. |
| `revocation_endpoint` | no | The revocation endpoint ([RFC 7009](https://www.rfc-editor.org/rfc/rfc7009)). |
| `code_challenge_methods_supported` | no | If present, it MUST contain `S256`. |

- Each URL MUST obey the HTTPS rule above.
- A service that has its own login, without an identity provider, publishes such a document for its own endpoints.

## Example

```json
{
  "custos_versions_supported": ["0"],
  "service_name": "Example Logistics",
  "service_documentation": "https://example.com/docs",
  "upload_endpoint": "https://example.com/api/v1/uploads",
  "upload_max_bytes": 4194304,
  "upload_min_interval_seconds": 60,
  "openid_configuration": "https://id.example.com/realms/custos/.well-known/openid-configuration",
  "client_id": "https://example.com/custos/client-metadata.json",
  "scopes": ["openid", "offline_access"]
}
```

## Refresh

Custos gets the discovery document and the metadata document of the authorization server when it starts, and when the user changes the service URL. Custos SHOULD get it again after an upload fails with `404`.
