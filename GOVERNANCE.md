# Governance

## Roles

- **Maintainer**: EternalDeiwos. Decides scope, accepts or rejects decisions, and merges to `main`.
- **Contributor**: any human or AI agent who proposes a change. Agents are Contributors.

## Decisions

An Architecture Decision Record (ADR) holds only a high-level architectural decision. Examples are how the projects relate to each other or to an outside party, and the process or the rules for agents. A decision inside one project needs no ADR. A new project or root folder needs an ADR only when the Maintainer asks for one.

The rules for ADRs are in [docs/adr/README.md](docs/adr/README.md). In short:

- A Contributor creates an ADR with the status `accepted`.
- An ADR is in force only when it is on `main` or on another shared branch. The merge of its pull request is its acceptance.
- Merge an ADR before or together with the change that depends on it.
- An ADR holds the case. The rule that applies now lives in the governing document, with a link to the ADR. A reader does not need the ADRs for routine work.

## Discussions

Discussions happen in GitHub issues and pull requests. A decision that does not need an ADR is recorded in the issue or the pull request that makes it. A note goes into the repository only when the Maintainer asks for it. It is a dated file `docs/notes/YYYY-MM-DD-slug.md`, and it is never authoritative.

## The specification

`spec/` is the authority for the contract between Custos and a server. Custos and the server conform to it. The server is a reference implementation, not the definition of the contract ([ADR 0004](docs/adr/0004-the-specification-is-the-authority.md)).

## Changes

1. Create a branch from the latest `main`.
2. Commit with a [Conventional Commits](https://www.conventionalcommits.org/) message. The scope is the project name, for example `feat(custos): watch the log folder` or `docs(spec): add the discovery document`. A change to the root files has no scope.
3. Open a pull request to `main`.

## CI and releases

- Each application has one workflow in `.github/workflows/`. It runs on a pull request and on a push to `main` that changes the project or `spec/`.
- Each project has its own version and its own `CHANGELOG.md`.
- `spec/` has the version `v0` until the Maintainer decides otherwise. It has no minor or patch versions and no release tags ([spec/04-versioning.md](spec/04-versioning.md)).
- A release of an application is a tag `<project>-X.Y.Z`, for example `server-0.2.0`. The Maintainer creates the tags. The releases before this format keep their tags `<project>/vX.Y.Z`: `custos/v0.1.0`, `custos/v0.2.0`, and `server/v0.1.0`.
- The tag `custos-X.Y.Z` builds the AppImage, the installer script, and the Windows installer. It attests the three files and attaches them to a GitHub release.
- The tag `server-X.Y.Z` publishes the container image `ghcr.io/atlashq-gg/custos-server`. Each push to `main` that changes the server publishes the tag `unstable`.

## Licenses

The content of this repository is under the MIT license, except `server/`, which is under the AGPL-3.0-or-later license, except `server/catalog/catalog.json`, which is under CC BY-NC-SA 4.0 ([server/catalog/LICENSE.md](server/catalog/LICENSE.md)). Each project folder holds the `LICENSE` file of its license ([ADR 0005](docs/adr/0005-license-by-project.md)).

## Documents

Each folder is authoritative for its content. A root file delegates downward with a link and does not repeat lower content. Each project folder has a `README.md`. Where agents work in a folder, it also has an `AGENTS.md` ([ADR 0003](docs/adr/0003-agent-instruction-hierarchy.md)).
