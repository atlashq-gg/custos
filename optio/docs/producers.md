# Producers

**Unstable.** This contract can change. The [changelog of Optio](../CHANGELOG.md) records each change.

A producer is a domain service that sends events to Optio, for example the [server](../../server/README.md). This document is the contract between a producer and Optio. The contract between Custos and Optio is [spec/](../../spec/README.md). The decision is [ADR 0007](../../docs/adr/0007-a-broker-carries-domain-events.md).

The key words MUST, MUST NOT, SHOULD, and MAY are as in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119).

## Overview

1. The producer commits a change.
2. The producer publishes one [event](#event) for each guild that can read the change, below its own subjects on the NATS JetStream broker. The event holds the text for the user and the [policy](#policy) that tells who can read it.
3. The broker keeps each event in the [stream](#broker) of Optio for the replay window.
4. Optio reads the stream. It sends the message of the event to each open stream of Custos whose user matches the policy ([spec/05-stream.md](../../spec/05-stream.md)).
5. When a user connects again, Optio replays the events that the user missed from the stream, with the policy of now. The replay window is 15 minutes by default.

The producer controls the access to its own APIs. With an event, it sends the authorization to read the event, and Optio applies it. Optio never sends the policy to Custos.

## Event

An event is a [CloudEvents 1.0](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md) event in the [JSON format](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/formats/json-format.md), in the body of a NATS message. It uses the structured mode of the [CloudEvents NATS binding](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/bindings/nats-protocol-binding.md): the body holds all attributes, and no header holds an attribute.

The schema is [schemas/event.schema.json](../schemas/event.schema.json).

| Attribute | Required | Content |
|---|---|---|
| `specversion` | yes | `1.0`. |
| `id` | yes | A unique value for each event of the source, for example a UUID. |
| `source` | yes | The name of the producer, for example `custos`. It is also the second token of the [subject](#message). |
| `type` | yes | The event type, for example `stockpile.changed`. Lowercase words with a `.` between them. |
| `time` | yes | The time when the producer recorded the change, as RFC 3339. |
| `datacontenttype` | no | `application/json`. |
| `data` | yes | The [data](#data) of the event. |
| `traceparent` | no | The W3C trace context of the change, so that a trace goes from the producer to Optio. |

An event MUST NOT have `data_base64`.

### Data

| Member | Type | Required | Content |
|---|---|---|---|
| `policy` | object | yes | Who can read the event ([Policy](#policy)). |
| `message` | object | yes | The message for the user. |
| `message.text` | string | yes | Plain text for the user, 500 characters at most. It can hold line breaks. Optio sends it as the `text` of the message. |
| `message.reference` | string | no | A message with the reference of a shown message replaces it. Optio sends it as the `reference` of the message. |

An event type can have more members in `data`. The producer documents them. Optio ignores a member that it does not know.

### Policy

| Member | Type | Required | Content |
|---|---|---|---|
| `version` | integer | yes | `1`. |
| `guild` | string | yes | The ID of the Discord guild that can read the event. |
| `roles` | array of string | yes | The IDs of the roles of `guild` that can read the event. It can be empty. |
| `users` | array of string | no | The IDs of the Discord users that can read the event. |

A Discord ID is a string, because it can be larger than the largest exact integer of JSON in many parsers.

Optio applies these rules. A producer does not add them to the policy.

1. A user reads an event only on a stream whose `active_guild` is `guild`, and only as a member of `guild`.
2. A user reads the event when one of the roles of the user is in `roles`, or when the user is in `users`. One match is sufficient.
3. An empty list gives access to nobody.
4. The ID of `guild` in `roles` is the role `@everyone`: each member of the guild, also a pending member.
5. The owner of the guild and a member with the permission `ADMINISTRATOR` read each event, also with an empty `roles`. This rule does not apply to an event with `users`.
6. Optio ignores a role ID that is not a role of `guild`.
7. Optio uses the roles of now, also for a replay. A role change applies after a short delay.
8. Optio does not send an event with a `version` that it does not know. It counts it.
9. When the bot of Optio is not a member of `guild`, Optio counts the event. It sends one `error` message to a stream of that guild when the stream opens.

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

| Part | Value |
|---|---|
| Subject | `events.<source>.<guild>.<type>`, with the `guild` of the policy, for example `events.custos.300000000000000002.stockpile.changed` |
| Header `Content-Type` | `application/cloudevents+json` |
| Header `Nats-Msg-Id` | The `id` of the event. |
| Body | The event as JSON. |

- The source holds only lowercase letters, digits, and `-`. The guild is the Discord ID in decimal digits. Thus no part before the type holds a `.`, a `*`, or a `>`.
- The subject MUST agree with the event: the second token is `source`, the third token is `policy.guild`, and the rest is `type`. Optio does not send an event whose subject does not agree with it, and counts it.
- Optio gets the source of an event from its subject. The permissions of the broker make sure that only the producer of a source publishes below it.

## Publish

- The producer MUST publish an event only after the commit of the change.
- The producer MUST NOT wait for the broker in the request that made the change. A change MUST succeed when the broker is not available.
- The producer publishes with the JetStream publish of its client, and uses the acknowledgement of the broker only for its counters.
- Delivery is at-most-once. The producer does not keep or publish again an event that the broker did not acknowledge. A lost event is acceptable after a failure or a restart, but not in normal operation. The header `Nats-Msg-Id` stops a second store of the same event in the duplicate window of the stream.
- The producer SHOULD let its client connect again when the broker is not available.
- The producer MUST count each event with no stream: the broker answers that no stream has the subject. This shows that the stream is missing.
- The producer SHOULD export its counters as metrics: events published, with no stream, and failed.

## Broker

Optio makes the stream when it starts, and sets its settings again at each start. The operator configures only the NATS users and their permissions, for example in [nats/nats.conf](../nats/nats.conf). A producer makes nothing on the broker.

| Setting | Value | Content |
|---|---|---|
| Name | `OPTIO` | |
| Subjects | `events.>`, `queries.>` | `queries.>` holds the query messages of Optio ([README](../README.md#query)). |
| Storage | file | |
| Retention | limits | |
| Maximum age | The replay window of Optio, 15 minutes by default | The broker removes each event at its age. |
| Maximum size | 100 MB by default | A limit against a flood of events. The broker then removes the oldest events. |
| Duplicate window | 2 minutes, or the maximum age when it is shorter | |
| Rollup, delete, purge | off | |

| User | Publish | Subscribe |
|---|---|---|
| The producer | `events.<source>.>` | `_INBOX.<user>.>` |
| `optio` | The JetStream API subjects of the stream `OPTIO` and of the buckets `OPTIO_QUERIES` and `OPTIO_CONVERSATIONS`, the bucket subjects, and `queries.>`, as in [nats/nats.conf](../nats/nats.conf) | `_INBOX.optio.>` |

Each client sets the inbox prefix `_INBOX.<user>`, so that it gets the replies of the broker on its own subjects. Thus a producer can publish only below its own source. It cannot read, and it cannot change the stream. No user reads the replies of another user. Optio does not publish events, and a producer does not publish query messages.

## Compatibility

- A producer MAY add a member to `data` or an attribute to an event.
- A change of the data of a type that is not compatible needs a new `type`, for example `stockpile.changed.v2`.
- A change of the policy that is not compatible needs a new `version`.
- The producer MUST NOT change the meaning of a member.

## Join as a new producer

A producer can use any language. It needs no MCP endpoint and no token for Optio. It needs:

1. A NATS client with JetStream, for example the NATS client of its language.
2. A source name. Use lowercase letters, digits, and `-`.
3. In the NATS configuration: a user with the permissions of [Broker](#broker).

Optio needs no change for a new producer.
