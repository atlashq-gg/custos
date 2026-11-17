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
| `custos_versions_supported` | array of strings | yes | The contract versions that the service supports ([versioning.md](versioning.md)). |
| `service_name` | string | yes | A short name that Custos shows to the user. |
| `service_documentation` | string (URL) | no | A page for humans about the service. |
| `upload_endpoint` | string (URL) | yes | The endpoint for the upload ([upload.md](upload.md)). |
| `upload_max_bytes` | integer | yes | The largest body that the upload endpoint accepts. |
| `upload_min_interval_seconds` | integer | yes | The shortest time between two uploads of one client. |
| `authorization_endpoint` | string (URL) | yes | The OAuth 2.0 authorization endpoint ([authentication.md](authentication.md)). |
| `token_endpoint` | string (URL) | yes | The OAuth 2.0 token endpoint. |
| `revocation_endpoint` | string (URL) | no | The OAuth 2.0 revocation endpoint ([RFC 7009](https://www.rfc-editor.org/rfc/rfc7009)). |
| `client_id` | string | yes | The client ID that Custos uses at the authorization server. |
| `scopes` | array of strings | yes | The scopes that Custos requests. It can be empty. |

Each URL in the document MUST use HTTPS, with the same exception for loopback hosts. The URLs can point to a different host than the service URL, for example to an identity provider.

## Example

```json
{
  "custos_versions_supported": ["0.1"],
  "service_name": "Example Logistics",
  "service_documentation": "https://example.com/docs",
  "upload_endpoint": "https://example.com/api/v1/uploads",
  "upload_max_bytes": 4194304,
  "upload_min_interval_seconds": 60,
  "authorization_endpoint": "https://id.example.com/realms/custos/protocol/openid-connect/auth",
  "token_endpoint": "https://id.example.com/realms/custos/protocol/openid-connect/token",
  "revocation_endpoint": "https://id.example.com/realms/custos/protocol/openid-connect/revoke",
  "client_id": "https://example.com/custos/client-metadata.json",
  "scopes": ["openid", "offline_access"]
}
```

## Refresh

Custos gets the discovery document when it starts and when the user changes the service URL. Custos SHOULD get it again after an upload fails with `404`.
