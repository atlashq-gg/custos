---
id: 0002
title: Monorepo with projects at the root
status: accepted
date: 2026-11-17
tags: [repository]
supersedes: []
superseded-by: []
---

# 0002 - Monorepo with projects at the root

## Context

The repository holds three projects. Custos is the overlay application in Rust and Tauri. The server is a reference implementation in .NET 10. The specification is the contract between them. The specification is not code in one language, and both applications depend on it. The projects change together when the contract changes, so one repository is the single source of truth. A reader must be able to find a project by its role.

## Decision

Use one repository, with one folder for each project at the root.

| Project | Folder |
|---|---|
| spec | `spec/` |
| custos | `custos/` |
| server | `server/` |

- The repository root holds the project folders, `docs/`, and information about the repository as a whole.
- Each project folder holds its own toolchain configuration.
- Each project folder has a `README.md` that states its purpose. A `README.md` holds the minimum, with links to other documents for more context.
- Each folder is authoritative for its content. Documentation is read from the root down, but the root delegates downward with links and does not repeat lower content.

## Options considered

| Option | For | Against |
|---|---|---|
| Projects at the root | Each path names a role. Paths do not depend on a language. Short paths. | Two projects in one language do not share a toolchain folder. |
| Projects grouped by ecosystem, for example `rust/custos/` | Toolchain configuration sits with its ecosystem. | The language-neutral `spec/` fits no ecosystem. Paths change if a project changes its language. |
| One repository per project | Small, independent histories. | A contract change needs pull requests in three repositories. |

## Consequences

- A reader finds a project by its role.
- Root files stay small, because content moves down to the folder that owns it.
- A new project adds one folder at the root and one row to the project table of the root `AGENTS.md`.

## Log

- 2026-11-17: Created.
- 2026-11-23: [ADR 0006](0006-optio-is-the-service-of-the-contract.md) adds a fourth project folder, `optio/`. This ADR does not change.
