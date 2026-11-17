# Upload

Custos sends the full map data file of Foxhole to the service. Custos does not parse the file.

## Request

```http
POST {upload_endpoint}
Authorization: Bearer {access_token}
Content-Type: application/octet-stream
Content-Length: {size}

{the full content of the map data file}
```

- The body is the full file, byte for byte.
- The request holds no file name and no metadata. The file name holds the Steam ID of the player, so Custos MUST NOT send it.
- The service identifies the player from the access token.
- The service gets other context, for example the current war, from other sources.

## Responses

| Status | Meaning | Action of Custos |
|---|---|---|
| `200` | The service accepted the file. The body is the [result document](#result-document). | Show the result to the user. |
| `401` | The token is missing or not valid. | Do the challenge steps in [authentication.md](authentication.md#challenge). |
| `404` | The endpoint does not exist. | Get the discovery document again. |
| `413` | The body is larger than `upload_max_bytes`. | Report the error. Do not send the same file again. |
| `415` | The `Content-Type` is not `application/octet-stream`. | Report the error. |
| `422` | The body is not a file that the service can use, for example not a GVAS file. | Report the error. Do not send the same file again. |
| `429` | The client sends too often. The response MUST have a `Retry-After` header. | Wait for the time in `Retry-After`. |
| `5xx` | An error in the service. | Try again at the next interval. |

Each error response has the type `application/problem+json` ([RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)). Custos MAY show its `title` and `detail` to the user.

## Result document

**Unstable.** This document can change in a MINOR contract version ([versioning.md](versioning.md#unstable-parts)). The schema is [schemas/upload-result.schema.json](schemas/upload-result.schema.json).

| Member | Type | Required | Content |
|---|---|---|---|
| `status` | string | yes | `accepted` in this version. |
| `received_at` | string (RFC 3339) | yes | The time when the service got the file. |
| `size_bytes` | integer | yes | The size of the file that the service got. |
| `sha256` | string | yes | The SHA-256 of the file, as 64 lowercase hexadecimal characters. |
| `war` | object or `null` | yes | The current war, or `null` if the service does not know it. |
| `war.war_id` | string | yes | The war ID from the Foxhole war API. |
| `war.war_number` | integer | yes | The war number. |
| `stockpiles` | array | yes | The stockpiles that the service found in the file. A service that does not parse the file sends an empty array. |
| `stockpiles[].name` | string | yes | The name of the stockpile. |
| `stockpiles[].location` | string | no | A name for the place of the stockpile. |
| `stockpiles[].item_count` | integer | no | The number of item entries in the stockpile. |

Custos makes the text of its notification from these members.

Example:

```json
{
  "status": "accepted",
  "received_at": "2026-11-17T14:05:00Z",
  "size_bytes": 254262,
  "sha256": "9f2c4e0b1d7a3f5e8c6b4a29170d3e5f8a1b2c3d4e5f60718293a4b5c6d7e8f9",
  "war": { "war_id": "1bb12ec2-e62d-4e77-a218-5c729e5b608e", "war_number": 142 },
  "stockpiles": []
}
```

## Interval

Custos sends a file only when all of these conditions are true:

1. Foxhole runs.
2. The user did not pause Custos.
3. The SHA-256 of the file is different from the SHA-256 of the last file that the service accepted with `200`.
4. The time since the last upload is at least the effective interval.

The effective interval is the largest of these values:

- The interval in the configuration of Custos.
- `upload_min_interval_seconds` from the discovery document.
- The time in the last `Retry-After` header, until that time is over.

A service SHOULD answer `429` to a client that sends more often than `upload_min_interval_seconds`.
