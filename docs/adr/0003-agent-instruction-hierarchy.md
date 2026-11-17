---
id: 0003
title: Agent instruction hierarchy
status: accepted
date: 2026-11-17
tags: [agents, documentation]
supersedes: []
superseded-by: []
---

# 0003 - Agent instruction hierarchy

## Context

AI agents contribute to this repository. An agent reads its instructions from files in the tree before it starts work. If the root file holds every rule for every project, it grows without limit and costs context on every task. If the rules are spread without a pattern, an agent misses them.

## Decision

Give each level of the tree its own `AGENTS.md`. The root holds the minimum. The nearest file wins.

- The root `AGENTS.md` holds only what an agent must know before it works in any project. That is the read order, the hard rules, and a table of project folders with links.
- Each project, and each folder inside it, may have an `AGENTS.md`. That file is authoritative for its folder and below.
- Where a rule in a lower file differs from a rule in a higher file, the lower file wins for its folder.
- `CLAUDE.md` at any level holds only an import of the `AGENTS.md` at the same level. `AGENTS.md` is the single source for agent instructions, and tool-specific files point to it.
- Content that belongs to one project does not go in a root file.

## Options considered

| Option | For | Against |
|---|---|---|
| One `AGENTS.md` per level, nearest wins | Small root. Rules sit with the code they govern. | An agent must read more than one file. |
| One root `AGENTS.md` with all rules | One file to read. | Grows without limit. Every task pays the full cost. |
| Tool-specific files only, such as `CLAUDE.md` | No indirection. | Each tool needs its own copy. Copies drift. |

## Consequences

- The root `AGENTS.md` stays under 40 lines.
- An agent reads the root file, then the project `README.md` and `AGENTS.md`. It reads an ADR only to learn the case for a rule, or to change a decision.
- Each new project folder adds one row to the root table.

## Log

- 2026-11-17: Created.
