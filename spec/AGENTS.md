# Agents: spec

- This folder is the authority for the contract ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)). A change here can need a change in `custos/` and `optio/`. Make them in the same pull request.
- Write the documents in ASD-STE100 style. Use RFC 2119 key words for requirements.
- Keep each JSON Schema in `schemas/` in step with the document that describes it. Both implementations test against these files.
- Do not add an OpenAPI document. The server generates its own.
- Give each document a two-digit prefix in reading order, for example `05-reports.md`. Update the table in `README.md`.
- Record each change in `CHANGELOG.md` under `v0`. Do not add a minor or patch version. Only the Maintainer changes the version.
