# Stream and query

**Unstable.** This document changes soon ([04-versioning.md](04-versioning.md#unstable-parts)).

A service can send messages to Custos on a stream, and it can answer a query of the user. Both parts are optional. A service without them works with the upload only, and Custos then works as without this document.

## Discovery

Two optional members of the discovery document ([01-discovery.md](01-discovery.md)) announce the parts:

| Member | Type | Content |
|---|---|---|
| `stream_endpoint` | string (URL) | The endpoint of the [stream](#stream). |
| `query_endpoint` | string (URL) | The endpoint of the [query](#query). A service with `query_endpoint` MUST also have `stream_endpoint`. |

Each URL obeys the HTTPS rule of the discovery document. Custos MUST ignore `query_endpoint` when `stream_endpoint` is not present.

## Stream

```http
GET {stream_endpoint}
Authorization: Bearer {access_token}
Accept: text/event-stream
Last-Event-ID: {id}
```

- `Last-Event-ID` is optional. Custos sends it only when it has the `id` of an event from an earlier connection ([Resume](#resume)).
- The response is `200` with `Content-Type: text/event-stream`, as in the [Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html) standard.
- The service MUST send a comment line at least each 30 seconds, also when it has no message.
- The service MAY close the stream at any time. It MUST close the stream when the access token expires, or before.

### Event

An event is a group of text lines, not a JSON document. Each line is one field, and an empty line ends the event. Thus the event has no JSON Schema. Only the value of `data` is JSON.

| Field | Required | Content |
|---|---|---|
| `id` | yes | An opaque value from the service. Custos sends it back in `Last-Event-ID` ([Resume](#resume)). It is one line of text with no line break. |
| `data` | yes | One [message](#message) as JSON on one line. The JSON encoding writes a line break in a string as `\n`, so the message needs no second `data` line. |

- The service MUST NOT send other fields. Custos MUST ignore a field that it does not know, for example `event` or `retry`.
- A line that starts with `:` is a comment. Custos ignores it. The service uses it to keep the connection open.

A stream with two events and a comment, as the service sends it:

```text
id: eyJjdXN0b3MiOiIxNDIifQ
data: {"kind":"event","reference":"stockpile:5f0c","text":"Fort Barley Seaport: 4 items changed.","source":"custos"}

: keep-alive

id: eyJjdXN0b3MiOiIxNDMifQ
data: {"kind":"event","reference":"stockpile:77a1","text":"Callahan's Gate depot: 2 items changed.","source":"custos"}

```

After these events, Custos keeps `eyJjdXN0b3MiOiIxNDMifQ`. When it connects again, it sends `Last-Event-ID: eyJjdXN0b3MiOiIxNDMifQ`.

### Resume

- Custos keeps the `id` of the last event that it got. When it connects again, it sends that `id` in `Last-Event-ID`.
- Custos MAY keep the `id` in memory only. After a restart, Custos connects without `Last-Event-ID`.
- The `id` is opaque to Custos.
- The service sends the messages after that `id` that it still has. The service decides how far back it goes.
- With an `id` that it does not accept, the service starts from now.
- A first connection, without `Last-Event-ID`, starts from now.

### Connect again

When the stream ends, or when a connection fails, Custos connects again with a valid access token. It waits 1 second after the first failure. It doubles the wait after each failure that follows, to 60 seconds at most. A stream that sent a message resets the wait.

### Responses

| Status | Meaning | Action of Custos |
|---|---|---|
| `200` | The stream is open. | Show the messages. |
| `401` | The token is missing or not valid. | Do the challenge steps in [02-authentication.md](02-authentication.md#challenge). |
| `403` | The service sends no stream to this user. | Do not connect again until the service URL or the user changes. |
| `404` | The endpoint does not exist. | Get the discovery document again. |
| `429` | The user has too many open streams. The response MUST have a `Retry-After` header. | Wait for the time in `Retry-After`. |
| `5xx` | An error in the service. | Connect again after the wait. |

## Message

The schema is [schemas/message.schema.json](schemas/message.schema.json).

| Member | Type | Required | Content |
|---|---|---|---|
| `kind` | string | yes | The [kind](#kinds) of the message. |
| `text` | string | no | Plain text for the user, 500 characters at most. Custos shows it as it is. |
| `reference` | string | no | A message with the reference of a shown message replaces it. A message with a reference and no `text` removes it. |
| `source` | string | no | A short name of the part of the service that made the message. |

Custos MUST NOT interpret `text` as markup.

### Kinds

The set of kinds is open. Custos MUST show a message of a kind that it does not know as `event`.

| Kind | Meaning |
|---|---|
| `event` | Something changed in the service, for example a stockpile. |
| `progress` | The state of a query. |
| `answer` | The full answer to a query, as one message. |
| `confirm` | A question to the user before the service does a change for a query. The user answers it through the [query endpoint](#confirmation). |
| `error` | A query or a part of the service failed. |

### Example

```json
{
  "kind": "event",
  "reference": "stockpile:5f0c",
  "text": "Fort Barley Seaport: 4 items changed.",
  "source": "custos"
}
```

## Query

```http
POST {query_endpoint}
Authorization: Bearer {access_token}
Content-Type: application/json

{"text": "How many rifles are in the seaport?"}
```

- The schema of the body is [schemas/query.schema.json](schemas/query.schema.json).
- `text` is the query of the user in plain language, 1 to 500 characters.
- The service answers on the stream. Thus a query needs an open stream of the user.
- The response `202` holds the reference of the query ([schemas/query-accepted.schema.json](schemas/query-accepted.schema.json)). Each message about the query has this reference.
- The service sends the messages of a query to each open stream of the user.
- The contract states only that the service answers. How the service makes the answer is not part of the contract.

```json
{ "reference": "query:8d1e" }
```

### Confirmation

A `confirm` message asks the user to accept or refuse a change. Its `reference` names the confirmation. Custos sends the answer of the user:

```json
{ "confirm": "confirm:41aa", "accept": true }
```

- `accept` is `true` when the user accepts, and `false` when the user refuses.
- The service MUST NOT do the change before it gets `accept` `true`.
- A confirmation expires. The service decides when.
- The response is `202` with the reference of the query.

### Responses

| Status | Meaning | Action of Custos |
|---|---|---|
| `202` | The service accepted the query or the answer. The messages come on the stream. | Wait for the messages. |
| `400` | The body is not valid. | Report the error. |
| `401` | The token is missing or not valid. | Do the challenge steps in [02-authentication.md](02-authentication.md#challenge). |
| `403` | The service does not answer queries from this user. | Report the error. |
| `404` | The endpoint does not exist. | Get the discovery document again. |
| `409` | The user has no open stream, or the confirmation is not open. | Report the error. |
| `429` | Too many queries. The response MUST have a `Retry-After` header. | Report the error. Do not send a query before the time in `Retry-After`. |
| `503` | The service cannot answer now. | Report the error. |

Each error response has the type `application/problem+json` ([RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)).
