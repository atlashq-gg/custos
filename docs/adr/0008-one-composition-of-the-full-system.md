---
id: 0008
title: One composition of the full system
status: accepted
date: 2026-11-28
tags: [architecture, repository]
supersedes: []
superseded-by: []
---

# 0008 - One composition of the full system

## Context

The full system has more than one service: Optio, the Server, the authorization server, the event broker, and the databases. Each service tests itself in isolation, against fakes or its own containers of the other parts. Nothing tests the services together, so a change in one service can break another service with no check before a release. The host that starts the full system for development is in one service, although it starts all of them. Debugging across the services needs the full system on one machine, with each service in one session of a debugger. More test suites across the services are to come. Each suite that starts the full system in its own way is one more composition to keep in step ([#90](https://github.com/atlashq-gg/custos/issues/90)).

## Decision

One composition of the full system is in `deploy/`, outside each service project. It is the one way to start the full system for development, debugging, and tests across the services.

- The composition uses the shared configuration of the deployment, for example the realm of the authorization server and the configuration of the broker.
- A test across the services starts this composition. It acts on the system only through public interfaces: the contract of `spec/` as a client, and the interfaces of the domain services.
- Each service keeps its own tests for its own behavior. A test across the services covers only behavior that needs more than one service.
- The composition and its tests have no version and no release.
- The composition starts without outside accounts, for example Discord or a model provider. A test that needs an outside account is not in the default CI run, as in the services.
- The cost of the tests has a limit. A check that the composition builds runs on each change of a part. The tests across the services run when the surface between the services changes, on request, and after each merge to `main`. A release of a service needs a passing run on `main`.
- Custos, the desktop application, is not part of the composition. It conforms to `spec/`, and the tests act in its role as a client.

## Options considered

| Option | For | Against |
|---|---|---|
| The host stays in one service | No move. | One service owns a part of all services. Its CI runs on changes of the other services. |
| A composition for each test suite | Each suite selects its parts. | Many compositions to keep in step with the services and with the deployment. |
| A new root project for the composition | A clear owner. | One more project with no version and no release, beside the shared configuration that it reads. |
| One composition in `deploy/` | One place for the shared configuration, the development host, and the tests across the services. | `deploy/` gets code and CI, and it is not only the deployment guide. |

## Consequences

- A developer starts and debugs the full system with one command or one IDE solution.
- A break in the surface between the services shows before the merge. A break inside a service shows at the latest after the merge, before a release.
- A change of a service runs one more cheap check.
- The tool of the composition is a shared dependency of the services. An upgrade of that tool is its own work.
- A test suite across the services adds tests to the composition, not a new structure.

## Log

- 2026-11-28: Created.
