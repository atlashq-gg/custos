# Producers

**Unstable.** This contract can change. The [changelog of `optio`](../CHANGELOG.md) records each change.

A producer is a domain service that sends events to `optio`, for example the [server](../../server/README.md). This document is the contract between a producer and `optio`. The contract between Custos and `optio` is [spec/](../../spec/README.md). The decision is [ADR 0007](../../docs/adr/0007-a-broker-carries-domain-events.md).

The key words MUST, MUST NOT, SHOULD, and MAY are as in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119).

## Overview

1. The producer commits a change.
2. The producer publishes one [event](#event) for each guild that can read the change, to its exchange on the RabbitMQ broker. The event holds the text for the user and the [policy](#policy) that tells who can read it.
3. The broker keeps each event in the [stream](#broker) of the producer.
4. `optio` reads the stream. It sends the message of the event to each open stream of Custos whose user matches the policy ([spec/05-stream.md](../../spec/05-stream.md)).
5. When a user connects again, `optio` replays the events of the last 15 minutes from the stream, with the policy of now.

The producer controls the access to its own APIs. With an event, it sends the authorization to read the event, and `optio` applies it. `optio` never sends the policy to Custos.

## Event

An event is a [CloudEvents 1.0](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md) event in the [JSON format](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/formats/json-format.md), in the body of an AMQP 0.9.1 message. The CloudEvents AMQP binding is for AMQP 1.0 only, so an event uses no AMQP header for an attribute.

The schema is [schemas/event.schema.json](../schemas/event.schema.json).

| Attribute | Required | Content |
|---|---|---|
| `specversion` | yes | `1.0`. |
| `id` | yes | A unique value for each event of the source, for example a UUID. |
| `source` | yes | The name of the producer, for example `custos`. It is also the name of the exchange after `events.`. |
| `type` | yes | The event type, for example `stockpile.changed`. Lowercase words with a `.` between them. |
| `time` | yes | The time when the producer recorded the change, as RFC 3339. |
| `datacontenttype` | no | `application/json`. |
| `data` | yes | The [data](#data) of the event. |
| `traceparent` | no | The W3C trace context of the change, so that a trace goes from the producer to `optio`. |

An event MUST NOT have `data_base64`.

### Data

| Member | Type | Required | Content |
|---|---|---|---|
| `policy` | object | yes | Who can read the event ([Policy](#policy)). |
| `message` | object | yes | The message for the user. |
| `message.text` | string | yes | Plain text for the user, 500 characters at most. It can hold line breaks. `optio` sends it as the `text` of the message. |
| `message.reference` | string | no | A message with the reference of a shown message replaces it. `optio` sends it as the `reference` of the message. |

An event type can have more members in `data`. The producer documents them. `optio` ignores a member that it does not know.

### Policy

| Member | Type | Required | Content |
|---|---|---|---|
| `version` | integer | yes | `1`. |
| `guild` | string | yes | The ID of the Discord guild that can read the event. |
| `roles` | array of string | yes | The IDs of the roles of `guild` that can read the event. It can be empty. |
| `users` | array of string | no | The IDs of the Discord users that can read the event. |

A Discord ID is a string, because it can be larger than the largest exact integer of JSON in many parsers.

`optio` applies these rules. A producer does not add them to the policy.

1. A user reads an event only on a stream whose `active_guild` is `guild`, and only as a member of `guild`.
2. A user reads the event when one of the roles of the user is in `roles`, or when the user is in `users`. One match is sufficient.
3. An empty list gives access to nobody.
4. The ID of `guild` in `roles` is the role `@everyone`: each member of the guild, also a pending member.
5. The owner of the guild and a member with the permission `ADMINISTRATOR` read each event, also with an empty `roles`. This rule does not apply to an event with `users`.
6. `optio` ignores a role ID that is not a role of `guild`.
7. `optio` uses the roles of now, also for a replay. A role change applies after a short delay.
8. `optio` does not send an event with a `version` that it does not know. It counts it.
9. When the bot of `optio` is not a member of `guild`, `optio` counts the event. It sends one `error` message to a stream of that guild when the stream opens.

A producer can address any guild or user. This is intentional. Thus only a trusted service gets a broker user that can publish.

### Shared records

When a guild shares its records with other guilds, the producer MUST publish one event for each guild that can read the change. These are the guild of the record and each reader guild.

- `policy.guild` is the reader guild. `policy.roles` holds the read roles of the reader guild.
- The text uses the names that the reader guild sees.
- The data SHOULD name the guild of the record, for example as `origin_guild`.

### Example

```json
{
  "specversion": "1.0",
  "id": "019ab8c0-6a3e-7c1f-9d2b-4e5f6a7b8c9d",
  "source": "custos",
  "type": "stockpile.changed",
  "time": "2026-11-24T18:03:12.512Z",
  "datacontenttype": "application/json",
  "data": {
    "policy": {
      "version": 1,
      "guild": "300000000000000002",
      "roles": ["300000000000000010", "300000000000000011"]
    },
    "message": {
      "text": "Fort Barley Seaport, Main\nBasic Materials -400 (0)\nRifle crates +20 (40)",
      "reference": "stockpile:5f0c0d1e-2f3a-4b5c-8d6e-7f8091a2b3c4"
    },
    "origin_guild": "300000000000000001"
  }
}
```

## Message

| AMQP property | Value |
|---|---|
| Exchange | `events.<source>` |
| Routing key | `<guild>.<type>`, with the `guild` of the policy, for example `300000000000000002.stockpile.changed` |
| `content_type` | `application/cloudevents+json` |
| `delivery_mode` | 2 (persistent) |
| `message_id` | The `id` of the event. |
| `mandatory` | true |

## Publish

- The producer MUST publish an event only after the commit of the change.
- The producer MUST NOT wait for the broker in the request that made the change. A change MUST succeed when the broker is not available.
- Delivery is at-most-once. The producer does not keep or publish again an event that the broker did not accept. A lost event is acceptable after a failure or a restart, but not in normal operation.
- The producer SHOULD connect again after a wait when the broker is not available.
- The producer MUST count each event that the broker returns. A returned event (`312 NO_ROUTE`) shows that the stream or its binding is missing.
- The producer MAY use publisher confirms, but only to count the events that the broker did not accept.
- The producer SHOULD export its counters as metrics: events published, returned, and failed.

## Broker

The operator makes the exchange, the stream, the binding, and the users in the definitions of the broker, for example [rabbitmq/definitions.json](../rabbitmq/definitions.json). A producer and `optio` declare nothing.

| Object | Name | Content |
|---|---|---|
| Exchange | `events.<source>` | Topic, durable. |
| Stream | `events.<source>.stream` | A queue with `x-queue-type` `stream`. |
| Binding | `events.<source>` to `events.<source>.stream` | Routing key `#`. |

The arguments of the stream:

| Argument | Value | Content |
|---|---|---|
| `x-max-age` | `1h` | The broker removes a segment when its messages are older. |
| `x-stream-max-segment-size-bytes` | `1000000` | Small segments, so that the broker removes old data sooner. |
| `x-max-length-bytes` | `100000000` | A limit against a flood of events. |

The broker removes data only when a segment rolls over. Thus an event can stay on the broker disk for longer than `x-max-age` when the producer publishes little. `optio` applies the replay window itself.

| User | Configure | Write | Read |
|---|---|---|---|
| The producer | `^$` | `^events\.<source>$` | `^$` |
| `optio` | `^$` | `^$` | `^events\..+$` |

Thus a producer can publish only to its own exchange, and it cannot read. `optio` can only read. It gets the source of an event from the name of its stream.

## Compatibility

- A producer MAY add a member to `data` or an attribute to an event.
- A change of the data of a type that is not compatible needs a new `type`, for example `stockpile.changed.v2`.
- A change of the policy that is not compatible needs a new `version`.
- The producer MUST NOT change the meaning of a member.

## Join as a new producer

A producer can use any language. It needs no MCP endpoint and no token for `optio`. It needs:

1. An AMQP 0.9.1 client, for example the RabbitMQ client of its language.
2. A source name. Use lowercase letters, digits, and `-`.
3. In the definitions of the broker: a user with its permissions, the exchange, the stream, and the binding, as in [Broker](#broker).
4. One entry with the source name in the configuration of `optio`.
