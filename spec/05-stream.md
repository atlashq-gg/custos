# Stream

**Unstable.** This document changes soon ([04-versioning.md](04-versioning.md#unstable-parts)).

A service can send messages to Custos on a stream. The stream is optional. A service without it works with the upload only, and Custos then works as without this document.

## Discovery

The optional member `stream_endpoint` of the discovery document ([01-discovery.md](01-discovery.md)) announces the stream:

| Member | Type | Content |
|---|---|---|
| `stream_endpoint` | string (URL) | The endpoint of the [stream](#stream). |

The URL obeys the HTTPS rule of the discovery document.

## Stream

```http
GET {stream_endpoint}
Authorization: Bearer {access_token}
Accept: text/event-stream
Last-Event-ID: {id}
```

- `Last-Event-ID` is optional. Custos sends it only when it has a position from an earlier connection ([Resume](#resume)).
- The response is `200` with `Content-Type: text/event-stream`, as in the [Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html) standard.
- The service MUST send a comment line at least each 30 seconds, also when it has no message.
- The service MAY close the stream at any time. It MUST close the stream when the access token expires, or before.

### Event

An event is a group of text lines, not a JSON document. Each line is one field, and an empty line ends the event. Thus the event has no JSON Schema. Only the value of `data` is JSON.

| Field | Required | Content |
|---|---|---|
| `id` | yes | The [position](#position) of the stream after this event. It is an opaque value from the service, one line of text with no line break. |
| `data` | no | One [message](#message) as JSON on one line. The JSON encoding writes a line break in a string as `\n`, so the message needs no second `data` line. An event with no `data` only sets the position. |

- The service MUST NOT send other fields. Custos MUST ignore a field that it does not know, for example `event` or `retry`.
- A line that starts with `:` is a comment. Custos ignores it. The service uses it to keep the connection open.

A stream with its position, two events, and a comment, as the service sends it:

```text
id: 1764090192512-141

id: 1764090192512-142
data: {"kind":"event","reference":"stockpile:5f0c","text":"Fort Barley Seaport: 4 items changed.","source":"custos"}

: keep-alive

id: 1764090192512-143
data: {"kind":"event","reference":"stockpile:77a1","text":"Callahan's Gate depot: 2 items changed.","source":"custos"}

```

After these events, Custos keeps `1764090192512-143`. When it connects again, it sends `Last-Event-ID: 1764090192512-143`.

### Position

- The `id` of each event is the position of the stream after that event.
- When the stream opens, the service MUST first send its position: an event with `id` and no `data`. Thus Custos has a position also before the first message.
- The service MAY send an event with no `data` at other times, to move the position.
- Custos keeps the `id` of the last event that it got, also of an event with no `data`. An event with no `data` gives no message. This agrees with the Server-Sent Events standard.

### Resume

- When Custos connects again, it sends its position in `Last-Event-ID`.
- Custos MAY keep the position in memory only. After a restart, Custos connects without `Last-Event-ID`.
- The position is opaque to Custos.
- The service sends the messages after that position that it still has. The service decides how far back it goes.
- With a position that it does not accept, the service starts from now.
- A first connection, without `Last-Event-ID`, starts from now.

### Connect again

When the stream ends, or when a connection fails, Custos connects again with a valid access token. It waits 1 second after the first failure. It doubles the wait after each failure that follows, to 60 seconds at most. A stream that sent a message resets the wait.

Custos waits a random time between half and all of the wait, also before the first try after a failure. Thus many clients do not connect at the same time, for example after a restart of the service.

The stream ends at the latest when the access token expires. Custos then connects again at once, with a new token.

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
| `error` | A part of the service failed, or the service cannot send the events of the user. |

### Example

```json
{
  "kind": "event",
  "reference": "stockpile:5f0c",
  "text": "Fort Barley Seaport: 4 items changed.",
  "source": "custos"
}
```
