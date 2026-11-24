# Producers

**Unstable.** This document changes until `optio` 0.1.0.

A producer is a domain service that sends events to `optio`, for example the [server](../../server/README.md). This document is the contract between a producer and `optio`. The contract between Custos and `optio` is [spec/](../../spec/README.md). The decision is [ADR 0007](../../docs/adr/0007-a-broker-signals-domain-events.md).

The key words MUST, MUST NOT, SHOULD, and MAY are as in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119).

## Overview

1. The producer commits a change.
2. The producer writes a [signal](#signal) to its exchange on the RabbitMQ broker. The signal names the guild that can read the event. It holds no domain data.
3. `optio` gets the signal when a user of that guild has an open stream.
4. `optio` calls the [poll tool](#poll-tool) of the producer, with a token of that user and the cursor of the stream.
5. The producer examines the access of the user, and returns the events after the cursor.
6. `optio` sends each event to Custos as a message of [spec/05-stream.md](../../spec/05-stream.md).

`optio` also calls the poll tool when a stream opens, and at an interval when the broker is not available. Thus a lost signal only delays an event.

## Signal

A signal is a [CloudEvents 1.0](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md) event in the [JSON format](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/formats/json-format.md), in the body of an AMQP 0.9.1 message. The CloudEvents AMQP binding is for AMQP 1.0 only, so a signal uses no AMQP header for an attribute.

The schema is [schemas/signal.schema.json](../schemas/signal.schema.json).

| Attribute | Required | Content |
|---|---|---|
| `specversion` | yes | `1.0`. |
| `id` | yes | A unique value for each signal of the source, for example a UUID. |
| `source` | yes | The name of the producer, for example `custos`. It is also the name of the exchange after `events.`. |
| `type` | yes | The event type, for example `stockpile.changed`. Lowercase words with a `.` between them. |
| `time` | yes | The time of the commit, as RFC 3339. |
| `readerguild` | yes | The ID of the Discord guild that can read the event, as a string. |
| `originguild` | yes | The ID of the Discord guild of the change, as a string. |
| `traceparent` | no | The W3C trace context of the change, so that a trace goes from the producer to `optio`. |

- A signal MUST NOT have `data` or `data_base64`. The access to an event depends on the user, so the event comes only from the poll tool.
- A guild ID is a string, because a Discord ID can be larger than the largest exact integer of JSON in many parsers.
- `optio` MUST ignore an attribute that it does not know.

Example:

```json
{
  "specversion": "1.0",
  "id": "019ab8c0-6a3e-7c1f-9d2b-4e5f6a7b8c9d",
  "source": "custos",
  "type": "stockpile.changed",
  "time": "2026-11-24T18:03:12.512Z",
  "readerguild": "300000000000000002",
  "originguild": "300000000000000001"
}
```

### Message

| AMQP property | Value |
|---|---|
| Exchange | `events.<source>` |
| Routing key | `<readerguild>.<type>`, for example `300000000000000002.stockpile.changed` |
| `content_type` | `application/cloudevents+json` |
| `delivery_mode` | 1 (transient). The broker keeps no signal. |
| `message_id` | The `id` of the signal. |
| `mandatory` | false. A signal without a queue is normal: no stream of that guild is open. |

### Rules

- The producer MUST write a signal only after the commit of the change. A poll before the commit does not find the event.
- The producer MUST write one signal for the origin guild, and one for each guild that can read the records of the origin guild. Each signal has its own `readerguild`.
- The producer MUST NOT wait for the broker in the request that made the change. A change MUST succeed when the broker is not available.
- The producer SHOULD use publisher confirms, and SHOULD try the connection again after a wait.
- Delivery is at-most-once. The producer does not keep a signal that the broker did not confirm.
- The producer MAY write one signal for more than one event of the same guild and type.

### Exchange and users

Each producer has one topic exchange, `events.<source>`, durable. The operator makes the exchange and the users in the definitions of the broker, for example [rabbitmq/definitions.json](../rabbitmq/definitions.json). A producer does not declare the exchange.

| User | Configure | Write | Read |
|---|---|---|---|
| The producer | `^$` | `^events\.<source>$` | `^$` |
| `optio` | `^amq\.gen-.*$` | `^amq\.gen-.*$` | `^(amq\.gen-.*\|events\..+)$` |

Thus a producer cannot write to the exchange of another producer, and it cannot read. `optio` binds its own queue, which the broker names, with `<guild>.#` while a stream of that guild is open.

A forged `readerguild` only causes calls to the poll tool of the same producer, which then examines the access.

## Poll tool

The producer serves the tool `poll_events` on its MCP endpoint. The tool takes the token of the user that `optio` gets from a token exchange. The producer examines the token, the scope, and the access of the user, as for its other tools.

### Arguments

| Argument | Type | Required | Content |
|---|---|---|---|
| `guild_id` | string | yes | The guild of the user, as in `readerguild`. |
| `cursor` | string or null | no | The `cursor` of the earlier result. Null or absent starts from now. |
| `max_age_seconds` | integer | no | The oldest event to return, in seconds before now. 1 to 900. The default is 900. |

### Result

The tool returns the result as JSON in one text content block. The schema is [schemas/poll-result.schema.json](../schemas/poll-result.schema.json).

| Member | Type | Content |
|---|---|---|
| `events` | array | The events after the cursor, in cursor order. It can be empty. |
| `cursor` | string | The cursor after the last event of `events`. With no event, the cursor of the call, or the cursor of now. |
| `truncated` | boolean | True when the producer skipped events after the cursor: the events were older than `max_age_seconds`, or the producer did not accept the cursor. |
| `has_more` | boolean | True when more events follow. `optio` then calls again at once with the new cursor. |

Each event:

| Member | Type | Required | Content |
|---|---|---|---|
| `id` | string | yes | A unique value for each event of the source. |
| `type` | string | yes | The event type, as in the signal. |
| `time` | string | yes | The time of the commit, as RFC 3339. |
| `text` | string | yes | Plain text for the user, 500 characters at most. `optio` sends it as the `text` of the message. |
| `reference` | string | no | A message with the reference of a shown message replaces it. `optio` sends it as the `reference` of the message. |

An event type can have more members. The producer documents them. A consumer that does not know a member MUST ignore it.

Example:

```json
{
  "events": [
    {
      "id": "019ab8c0-6a3e-7c1f-9d2b-4e5f6a7b8c9d.5f0c0d1e-2f3a-4b5c-8d6e-7f8091a2b3c4",
      "type": "stockpile.changed",
      "time": "2026-11-24T18:03:12.512Z",
      "text": "Fort Barley Seaport, Main: 4 items changed.",
      "reference": "stockpile:5f0c0d1e-2f3a-4b5c-8d6e-7f8091a2b3c4"
    }
  ],
  "cursor": "eyIzMDAwMDAwMDAwMDAwMDAwMDEiOjE0Mn0",
  "truncated": false,
  "has_more": false
}
```

### Rules

- The cursor is opaque to `optio`. `optio` keeps it in the SSE `id` of the stream and gives it back without a change.
- A call with the `cursor` of a result returns only events after the events of that result.
- A call without a cursor returns no events, and the cursor of now. This is the start of a new stream.
- The producer MUST NOT return an event that the user cannot read when the call occurs.
- The producer MUST keep its events for 900 seconds at least, or set `truncated` when it skipped some.
- A cursor that the producer does not accept, for example from an earlier version, starts from now, with `truncated` true. It is not an error.

### Refusals and failures

- A refusal is a tool result with `isError` true, with a problem body (RFC 9457) as JSON in the text content block. The member `status` of the body tells `optio` what to do:
  - `4xx`: the user cannot read the events. `optio` stops the calls to this producer for the stream.
  - `5xx`: a temporary failure. `optio` tries again after a wait.
- A JSON-RPC error, an HTTP error, or no answer is a failure. `optio` tries again after a wait.
- An HTTP `401` means that the token is not valid. `optio` gets a new token by exchange before the next call.

`optio` hides the poll tool from the model of its query harness.

## Compatibility

- A producer MAY add a member to an event or an attribute to a signal.
- A change that is not compatible needs a new `type`, for example `stockpile.changed.v2`.
- The producer MUST NOT change the meaning of a member.

## Join as a new producer

A producer can use any language. It needs:

1. An AMQP 0.9.1 client, for example the RabbitMQ client of its language.
2. A source name. Use lowercase letters, digits, and `-`.
3. A broker user and an exchange in the definitions of the broker, as in [Exchange and users](#exchange-and-users).
4. An MCP endpoint with the tool `poll_events`, behind the same authorization server as `optio`.
5. The read scope of the producer on the client `optio` in the realm, so that the token exchange gives a token for the producer.
6. One entry in the configuration of `optio` (`Optio:Sources`): the name, the MCP endpoint, and the read scope.
