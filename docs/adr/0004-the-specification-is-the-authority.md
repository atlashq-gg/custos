---
id: 0004
title: The specification is the authority
status: accepted
date: 2026-11-17
tags: [architecture]
supersedes: []
superseded-by: []
---

# 0004 - The specification is the authority

## Context

Custos reports to a remote server. It finds the configuration and the authentication context of a compliant service through a discovery mechanism. The repository also holds a server that implements the contract. Third parties will implement their own compliant services. If the code of one application defines the contract, the other application and every third party must read that code to conform. Then a change in the code changes the contract without notice.

## Decision

`spec/` defines the contract. custos and server conform to it.

- The contract covers the discovery mechanism, the authentication, and the reports that Custos sends.
- The server is a reference implementation of the contract, not its definition. A behavior of the server that `spec/` does not state is not part of the contract.
- A third party can implement a compliant service from `spec/` alone.
- A pull request may change `spec/` and an application together. The files in `spec/` are the authority for that change.
- The format of the specification, its versions, and its compatibility rules are decisions of the spec project. They need no ADR.

## Options considered

| Option | For | Against |
|---|---|---|
| `spec/` as the authority | One source for all implementations. Language-neutral. | One more project to keep in step with the code. |
| The server code as the authority | No separate files to keep. | Third parties must read .NET code. A refactor can change the contract without notice. |

## Consequences

- custos and server each test against `spec/`, not against each other.
- The spec project must define how Custos and a service agree on a contract version.

## Log

- 2026-11-17: Created.
