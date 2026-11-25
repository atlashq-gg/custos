---
id: 0006
title: Optio is the service of the contract
status: accepted
date: 2026-11-23
tags: [architecture, repository]
supersedes: [0004]
superseded-by: []
---

# 0006 - Optio is the service of the contract

## Context

Custos reports to a remote service. It finds the configuration and the authentication context of a compliant service through a discovery mechanism. Third parties can implement their own compliant services. If the code of one application defines the contract, every other implementation must read that code, and a change in the code changes the contract without notice.

Custos sends the map data file to the server, which also serves the discovery document and the Client ID Metadata Document of Custos. Custos is to get events from the services of a guild on a stream, and to send queries in plain language that an LLM harness answers with MCP tools. More than one service supplies the events and the tools. A desktop program cannot hold the model key of the operator. A harness in the server shares a process with the bot token and the database. Each action must run as the user, with the rights of the user, and no LLM gets administrative access to a service ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Decision

`spec/` defines the contract. A new project, `optio/`, is the one service that Custos calls, and it implements that contract. The server becomes a domain service behind Optio.

The contract:

- `spec/` covers the discovery mechanism, the authentication, the upload, the stream, and the query.
- Custos and Optio conform to `spec/`. Optio is an implementation of the contract, not its definition. A behavior of an implementation that `spec/` does not state is not part of the contract.
- A third party can implement a compliant service from `spec/` alone.
- A pull request may change `spec/` and an implementation together. The files in `spec/` are the authority for that change.
- The format of the specification, its versions, and its compatibility rules are decisions of the spec project. They need no ADR.

The services:

- Custos calls only Optio and the authorization server.
- Optio serves the discovery document and the Client ID Metadata Document of Custos. It relays the upload, sends the stream, and answers the query.
- Optio calls a domain service only with a token of the user for that service. It gets that token through a token exchange (RFC 8693). It has no service account at a domain service.
- Each domain service decides the access to its own APIs. Optio holds no records and no access settings of a domain service.
- Optio controls the access to the stream. It applies the policy that a domain service sends with each event ([ADR 0007](0007-a-broker-carries-domain-events.md)).
- Optio has its own Discord bot token. It uses the bot for the roles of the users of the stream, and later for a Discord front end.
- The upload, the stream, and the query do not depend on each other. The upload and the stream work with no model key.
- `optio/` is under AGPL-3.0-or-later, because an operator hosts it.

## Options considered

| Option | For | Against |
|---|---|---|
| `spec/` as the authority | One source for all implementations. Language-neutral. | One more project to keep in step with the code. |
| The code of an implementation as the authority | No separate files to keep. | Third parties must read .NET code. A refactor can change the contract without notice. |
| Optio as the one service of the contract | One caller for Custos. The harness and the model key are in one service that holds no records. More domain services join with configuration. | One more service to deploy. Custos 0.2.0 users log in again, because the client ID changes. |
| Custos calls the server for the upload, and Optio for the stream and the query | The upload does not depend on Optio. | Two services for one client. The Maintainer rejected it. |
| A harness in Custos | No new service. | A desktop program cannot hold the model key. A Discord front end needs a second harness. |
| A harness in the server | No new service. | The harness shares a process with the bot token and the database. Other services stay out of reach. |

## Consequences

- Custos and Optio each test against `spec/`, not against each other.
- The server is no longer the implementation of the contract. Its upload API is internal, and it needs no public route except `/mcp` and its metadata.
- The realm gets the confidential client `optio` and the scope `custos:client`.
- The operator adds the bot of Optio to each guild that uses the stream.
- The repository has four projects, each with its own version and release tag.

## Log

- 2026-11-23: Created.
