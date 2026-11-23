---
id: 0006
title: optio is the service of the contract
status: accepted
date: 2026-11-23
tags: [architecture, repository]
supersedes: []
superseded-by: []
---

# 0006 - optio is the service of the contract

## Context

Custos sends the map data file to the server, which also serves the discovery document and the Client ID Metadata Document of Custos. Custos is to get events from the services of a guild on a stream, and to send queries in plain language that an LLM harness answers with MCP tools. More than one service supplies the events and the tools. A desktop program cannot hold the model key of the operator. A harness in the server shares a process with the bot token and the database. Each action must run as the user, with the rights of the user, and no LLM gets administrative access to a service ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Decision

A new project, `optio/`, is the one service that Custos calls. It implements the contract of `spec/`. The server becomes a domain service behind it.

- Custos calls only `optio` and the authorization server.
- `optio` serves the discovery document and the Client ID Metadata Document of Custos. It relays the upload, sends the stream, and answers the query.
- `optio` calls a domain service only with a token of the user for that service. It gets that token through a token exchange (RFC 8693). It has no service account at a domain service.
- Each domain service decides the access of each request. `optio` holds no access rules and no records of a domain service.
- The upload, the stream, and the query do not depend on each other. The upload and the stream work with no model key.
- `optio/` is under AGPL-3.0-or-later, because an operator hosts it.

## Options considered

| Option | For | Against |
|---|---|---|
| `optio` as the one service of the contract | One caller for Custos. The harness and the model key are in one service that holds no records. More domain services join with configuration. | One more service to deploy. Custos 0.2.0 users log in again, because the client ID changes. |
| Custos calls the server for the upload, and `optio` for the stream and the query | The upload does not depend on `optio`. | Two services for one client. The Maintainer rejected it. |
| A harness in Custos | No new service. | A desktop program cannot hold the model key. A Discord front end needs a second harness. |
| A harness in the server | No new service. | The harness shares a process with the bot token and the database. Other services stay out of reach. |

## Consequences

- The server is no longer the implementation of the contract. Its upload API is internal, and it needs no public route except `/mcp` and its metadata.
- The realm gets the confidential client `optio` and the scope `custos:client`.
- The repository has four projects, each with its own version and release tag.

## Log

- 2026-11-23: Created.
