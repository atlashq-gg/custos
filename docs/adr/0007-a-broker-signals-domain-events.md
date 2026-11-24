---
id: 0007
title: A broker signals domain events
status: accepted
date: 2026-11-23
tags: [architecture]
supersedes: []
superseded-by: []
---

# 0007 - A broker signals domain events

## Context

`optio` sends the events of the domain services to Custos on a stream ([ADR 0006](0006-optio-is-the-service-of-the-contract.md)). The first event is a change of a stockpile, and more services will send events later. The access to an event depends on the live Discord roles of the member and on the sharing settings of the guild, which only the domain service knows. A broker cannot examine them, and `optio` must not hold the records of all guilds ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Decision

A RabbitMQ broker carries signals from the domain services to `optio`. A signal holds no domain data, and each domain service decides the access when `optio` gets the events.

- A signal names the source, the event type, and the guild that can read the event. It is a CloudEvents 1.0 document as JSON.
- Each domain service writes signals only to its own topic exchange. `optio` only reads, through queues that the broker names.
- After a signal, `optio` calls the poll tool of the source on its MCP endpoint, with a token of the user. The source examines the access and the sharing, and returns the events after a cursor.
- The source keeps the events. A replay comes from the source, not from the broker.
- A domain service writes a signal after its commit and does not wait for the broker. A lost signal delays an event until the next signal or the next connection.
- The contract for a producer is in `optio/`, not in `spec/`.

## Options considered

| Option | For | Against |
|---|---|---|
| RabbitMQ with signals | The access stays in the domain service. One place where new producers publish. More instances of `optio` are possible. | One more container. Each signal costs one call to the source. |
| RabbitMQ with the event text | Fewer calls to the sources. | `optio` gets the events of all guilds and filters them. It needs an outbox and a stream offset. |
| The MCP Events extension | A standard protocol on the MCP endpoint. | The MCP C# SDK does not implement it. One open request for each user and each source. |
| An HTTP callback from the producer to `optio` | No broker. | Each producer must reach each instance of `optio`. |
| NATS JetStream | Its auth callout fits an access check in the broker. | The access still depends on the Discord roles. |

## Consequences

- The deployment gets a broker, with one user for each producer and one for `optio`.
- Each domain service gets a poll tool and a publisher.
- A role change takes effect at the next token, because the server keeps an access check for each token.

## Log

- 2026-11-23: Created.
