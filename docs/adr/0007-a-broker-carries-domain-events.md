---
id: 0007
title: A broker carries domain events with their access policy
status: accepted
date: 2026-11-23
tags: [architecture]
supersedes: []
superseded-by: []
---

# 0007 - A broker carries domain events with their access policy

## Context

Optio sends the events of the domain services to Custos on a stream ([ADR 0006](0006-optio-is-the-service-of-the-contract.md)). The first event is a change of a stockpile, and more services will send events later. The access to an event depends on the Discord roles of the member and on the sharing settings of the guild. Only the domain service knows the sharing settings. A user can connect again after a short interruption and must then get the events that it missed. A lost event is acceptable after a failure, but not in normal operation ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Decision

A NATS JetStream broker carries each domain event to Optio with its data and with a policy that tells who can read it. Optio applies the policy to the stream of each user.

- The domain service is the authority for the access to its data. It controls the access to its own APIs. When it sends data to another service, it sends the authorization to read that data with it.
- The policy is in Discord terms: a guild, and the roles or the users that can read the event. The domain service applies its sharing settings: it sends one event for each guild that can read the record.
- Optio gets the roles of each connected user from Discord with its own bot. It applies the policy, and sends an event only to a stream whose user matches it. The broker does not control the access.
- The broker keeps the events for the replay window, and removes each event at the end of the window. Its sequence gives the position of a client. Optio reads the events that a user missed from the broker, and keeps no events of its own.
- Optio owns the stream on the broker, and makes it when it starts. The operator defines only the broker users and their permissions.
- A domain service publishes an event after its commit, and does not wait for the broker. It counts the events that do not arrive.
- Each domain service publishes only in its own part of the broker, and cannot read. Optio does not publish events.
- The contract for a producer is in `optio/`, not in `spec/`.

## Options considered

| Option | For | Against |
|---|---|---|
| Events with their data and policy on NATS JetStream | One message for each event and each reader guild. No call back to the source. The broker removes each event at its age, and gives one sequence for each position. A reader for each client stream needs no store in Optio. Optio can make the stream itself. | Optio gets the events of all guilds and must apply the policy correctly. Optio needs a bot and the roles of each connected user. Event data stays on the broker disk for the replay window. |
| The same events on RabbitMQ streams | A well-known broker. | The broker removes data only by full segments, so Optio must keep its own replay window and log. An offset is for one stream only, and starts at 0 again when the stream is made again. The first connection with no broker blocked the host. The operator must declare the stream. |
| NATS JetStream with an auth callout | The broker examines the access of each reader. | The access depends on the live Discord roles, which the broker does not know. The Maintainer rejected it. |
| Signals with no data, then a poll of the source with a token of the user | The source decides each access at the time of the read. Optio holds no member data. | Each signal costs one more call to the source. The bus carries no data. Each source must keep the events and a cursor. The Maintainer rejected it. |
| The MCP Events extension | A standard protocol on the MCP endpoint. | The MCP C# SDK does not implement it. One open request for each user and each source. |
| An HTTP callback from the producer to Optio | No broker. | Each producer must reach each instance of Optio. No replay. |

## Consequences

- The deployment gets a NATS server with JetStream, with one user for each producer and one for Optio.
- Each domain service gets a publisher. It needs no tool to serve events.
- Optio gets a Discord bot token and a gateway connection, and it holds the roles of the connected users in memory.
- A role change takes effect in Optio after a short delay. A stream continues until the token of the user expires.
- The query messages of Optio can use the same stream later, so that a client has one position.

## Log

- 2026-11-23: Created.
