# ADR index

Architecture Decision Records (ADRs) for high-level architectural decisions. An ADR records a decision and its case. The rule that applies now lives in the governing document, with a link here. Open an ADR to learn why a rule exists, or to change it.

A decision inside one project needs no ADR. Record it in the issue or the pull request that makes it. Retired records go to [archive/](archive/README.md).

| ID | Title | Status | Tags | Summary |
|---|---|---|---|---|
| [0000](0000-template.md) | Template | accepted | process | Copy this file to start a new ADR. |
| [0001](0001-record-decisions-as-adrs.md) | Record decisions as ADRs | accepted | process, documentation | Record each high-level architectural decision as a MADR file with frontmatter, a sequential ID, and a row in this index. |
| [0002](0002-monorepo-with-projects-at-the-root.md) | Monorepo with projects at the root | accepted | repository | One repository, with one folder for each project at the root: `spec/`, `custos/`, and `server/`. |
| [0003](0003-agent-instruction-hierarchy.md) | Agent instruction hierarchy | accepted | agents, documentation | One AGENTS.md per level. The root holds the minimum. The nearest file wins. CLAUDE.md only imports it. |
| [0005](0005-license-by-project.md) | License by project | accepted | repository | spec, custos, and the root content are under MIT, and server is under AGPL-3.0-or-later, with one `LICENSE` file in each project folder. |
| [0006](0006-optio-is-the-service-of-the-contract.md) | Optio is the service of the contract | accepted | architecture, repository | `spec/` defines the contract, Custos calls only Optio, which implements it with tokens of the user, and the server becomes a domain service behind it. Supersedes 0004. |
| [0007](0007-a-broker-carries-domain-events.md) | A broker carries domain events with their access policy | accepted | architecture | A NATS JetStream broker carries each domain event with its data and an access policy in Discord terms, and Optio applies the policy to the stream of each user and replays from the broker. |
| [0008](0008-one-composition-of-the-full-system.md) | One composition of the full system | accepted | architecture, repository | One composition of the full system in `deploy/` is the one way to start it for development, debugging, and tests across the services. |

## Tags

| Tag | Use for |
|---|---|
| `process` | How decisions and changes are made |
| `repository` | Repository structure and layout |
| `agents` | Rules for AI agents |
| `documentation` | Documents, READMEs, and indexes |
| `architecture` | How the projects relate to each other and to outside parties |

Add a new tag to this table in the same commit that first uses it. Keep the list under ten tags.

## Lifecycle

```
accepted --> deprecated
    |
    v
superseded
```

- Copy [0000-template.md](0000-template.md) to `NNNN-slug.md`. Use the next free ID, including IDs in the archive.
- Add one row to the index table in the same commit. The summary is one sentence that states the decision.
- A Contributor creates an ADR with the status `accepted`. The ADR is in force only when it is on `main` or on another shared branch. The merge of its pull request is its acceptance.
- Before the merge, an ADR changes freely. If the pull request closes without a merge, the ADR does not exist.
- Put the rule that applies into the governing document, with a link to the ADR, in the same pull request.
- After the merge, only the status fields and the Log section may change. Any other change needs a new ADR that lists the old ID in `supersedes`. The old ADR gets status `superseded` and lists the new ID in `superseded-by`.
- A Contributor changes the status of a merged ADR only at the Maintainer's instruction.
- When an ADR becomes `deprecated` or `superseded`, move the file to `archive/` and move its row to the archive index.
- When this table passes 15 rows, the next change to a topic must consolidate. The new ADR restates the full current position on that topic and lists each replaced ID in `supersedes`.
- An ADR is self-contained. It does not depend on the content of a superseded ADR.
- If two branches use the same ID, the branch that merges second renumbers its ADR and its index row.

The full rules are in [0001](0001-record-decisions-as-adrs.md).
