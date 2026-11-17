# Agents: spec

- This folder is the authority for the contract ([ADR 0004](../docs/adr/0004-the-specification-is-the-authority.md)). A change here can need a change in `custos/` and `server/`. Make them in the same pull request.
- Write the documents in ASD-STE100 style. Use RFC 2119 key words for requirements.
- Keep each JSON Schema in `schemas/` in step with the document that describes it. Both implementations test against these files.
- Do not add an OpenAPI document. The server generates its own.
- Record each change in `CHANGELOG.md` under `v0`. Do not add a minor or patch version. Only the Maintainer changes the version.
