---
id: 0005
title: License by project
status: accepted
date: 2026-11-17
tags: [repository]
supersedes: []
superseded-by: []
---

# 0005 - License by project

## Context

The repository holds three projects with different audiences. Third parties implement the specification in their own services. Players install Custos. Operators host the server. On 2026-11-17 the Maintainer chose a split license: "Contract and Custos MIT; Server AGPL-3.0". The Maintainer then set `AGPL-3.0-or-later` for the server, and "The Custos contributors" as the copyright holder, because Atlas does not use real names. A license that differs by folder must be clear to a reader, to a package registry, and to GitHub.

## Decision

spec, custos, and the root content are under the MIT license. server is under the AGPL-3.0-or-later license.

| Content | License |
|---|---|
| `spec/` | MIT |
| `custos/` | MIT |
| `server/` | AGPL-3.0-or-later |
| All other content, including the root files and `docs/` | MIT |

- The root `LICENSE` holds the MIT text. It covers all content outside `server/`.
- Each project folder holds a `LICENSE` file with the full text of its license. The commit that creates the project adds the file.
- The package metadata of each project states its license with an SPDX identifier, for example `license = "MIT"` in `Cargo.toml` and `AGPL-3.0-or-later` for server.
- The copyright holder in each license text is "The Custos contributors". Atlas does not use real names, and no license text names a person.
- A contribution takes the license of the folder that it changes. No contributor license agreement applies.

## Options considered

| Option | For | Against |
|---|---|---|
| MIT for spec and custos, AGPL-3.0-or-later for server | Third parties implement the contract and embed Custos with no copyleft. A party that hosts a modified server must share its changes with its users. | Two licenses to explain. |
| One permissive license for all | Simplest. | A party can host a modified server and keep its changes closed. |
| One copyleft license for all | All changes stay open. | Third parties hesitate to implement the contract. |

## Consequences

- A third party can write a closed service from the specification. The AGPL-3.0-or-later applies only to a copy or a modification of the server code.
- GitHub shows MIT for the repository, because it reads the root `LICENSE`. The root `README.md` must state the exception for `server/`.

## Log

- 2026-11-17: Created.
- 2026-11-21: The Maintainer added `server/catalog/catalog.json`, a file from the Foxhole wiki under CC BY-NC-SA 4.0 ([server/catalog/LICENSE.md](../../server/catalog/LICENSE.md)). It is a data file inside `server/`, and it is the only file in the repository under that license. This ADR does not change: the code of `server/` stays under AGPL-3.0-or-later. The NonCommercial term applies to the container image that holds the file.
- 2026-11-23: `optio/` is under AGPL-3.0-or-later, as `server/`, because an operator hosts it ([ADR 0006](0006-optio-is-the-service-of-the-contract.md)). This ADR does not change.
