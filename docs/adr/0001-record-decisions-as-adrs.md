---
id: 0001
title: Record decisions as ADRs
status: accepted
date: 2026-11-17
tags: [process, documentation]
supersedes: []
superseded-by: []
---

# 0001 - Record decisions as ADRs

## Context

Custos is a monorepo with several projects, and both human and agent contributors. A decision about the architecture as a whole is made in conversation and then lost. An agent that starts work has no single place to learn what was decided, why, and whether the decision still holds. Most decisions are smaller. They concern one project, and their issue or pull request records them well enough. A record of every decision costs more than it gives.

## Decision

Record each high-level architectural decision as an Architecture Decision Record (ADR) in `docs/adr/`, with the rules below.

Scope:

- An ADR holds only a high-level architectural decision. Examples are how the projects relate to each other or to an outside party, and the process or the rules for agents.
- A decision inside one project needs no ADR. The issue or the pull request that makes it records it.
- A new project or root folder needs an ADR only when the Maintainer asks for one.
- The repository has one ADR folder, at `docs/adr/`.

Format:

- Each ADR is one Markdown file with YAML frontmatter, in the MADR style.
- The frontmatter fields are `id`, `title`, `status`, `date`, `tags`, `supersedes`, and `superseded-by`.
- The body sections are Context, Decision, Options considered, Consequences, and Log, in that order.
- The file name is `NNNN-slug.md`, where `NNNN` is a sequential 4-digit ID.
- The template is ADR `0000`.

Index and tags:

- `docs/adr/README.md` holds an index table with one row per ADR. Each row has a one-sentence summary of the decision. Thus a reader gets the decision from the table, and opens the file only for the case behind it. The author updates the row in the same commit as the ADR.
- The index holds the list of allowed tags. A new tag goes into the list in the same commit that first uses it. The list stays under ten tags.

Working set:

- An ADR is a record of a decision and its case. The rule that applies now lives in the document that governs the work, with a link to the ADR. Examples are `AGENTS.md`, `GOVERNANCE.md`, and a `README.md`. A reader opens an ADR to learn why a rule exists, or to change it. A reader does not need the ADRs to do routine work.
- Records with status `deprecated` or `superseded` move to `docs/adr/archive/`, with their own index table. The main folder and the main table hold only records that are in force.
- When the main table passes 15 rows, the next change to a topic consolidates. The new ADR restates the current position on that topic in full and lists each ADR it replaces in `supersedes`. The replaced records move to the archive.

Lifecycle:

- Status is one of `accepted`, `deprecated`, or `superseded`.
- A Contributor creates an ADR with the status `accepted`. The ADR is in force only when it is on `main` or on another shared branch. The merge of its pull request by the Maintainer is its acceptance.
- Before the merge, an ADR changes freely. If its pull request closes without a merge, the ADR does not exist.
- After the merge, only the status fields and the Log section may change. Any other change needs a new ADR that supersedes the old one.
- A Contributor changes the status of a merged ADR only at the Maintainer's instruction.
- An ADR is self-contained. It makes its full case without content from a superseded ADR. It names the superseded ADR by ID only.
- An ADR merges before or together with the change that depends on it.
- If two branches use the same ID, the branch that merges second renumbers its ADR and its index row.

## Options considered

| Option | For | Against |
|---|---|---|
| ADRs for high-level decisions only | Few records, each one important. Project work moves without ceremony. | A project decision lives only in an issue or a pull request. |
| ADRs for all decisions, with a folder per project | One kind of record for every decision. | Many records of small decisions. Each change to a project costs an ADR. |
| A `proposed` status before acceptance | The status shows an open decision. | The pull request already shows an open decision. An extra status change for each ADR. |
| MADR with YAML frontmatter | Machine-readable status, date, tags, and links. Agents filter without a body read. | More fields to keep correct. |
| Nygard minimal | Shortest to write. | Status and links live in prose. Hard to query. |

## Consequences

- A reader gets the high-level decisions from one index table.
- Agents can filter ADRs by frontmatter fields with standard text tools.
- Routine work does not require a read of any ADR. The governing documents carry the rules.
- The history of a project decision is in GitHub, not in the repository.
- The active table has an upper bound in practice, because consolidation is required past 15 rows.

## Log

- 2026-11-17: Created.
