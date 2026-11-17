# Custos specification

Version `v0`. The specification has no minor or patch versions ([versioning.md](versioning.md)).

This project defines the contract between Custos and a compliant service. Custos and the reference server conform to it. A third party can implement a compliant service from these documents alone ([ADR 0004](../docs/adr/0004-the-specification-is-the-authority.md)).

## Documents

| Document | Content |
|---|---|
| [versioning.md](versioning.md) | Contract versions and how Custos and a service agree on one. |
| [discovery.md](discovery.md) | The discovery document that Custos gets from one URL. |
| [authentication.md](authentication.md) | The browser login and the access tokens. |
| [upload.md](upload.md) | The upload of the map data file, the responses, and the upload interval. |
| [schemas/](schemas/) | JSON Schemas (draft 2020-12) for the JSON documents. |

## Conventions

- The key words MUST, MUST NOT, SHOULD, and MAY are as in [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119).
- JSON member names use `snake_case`.
- A service MUST use HTTPS. Custos MAY use HTTP only for a host that is `localhost` or a loopback address.
- An error response uses `application/problem+json` ([RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)).
- A service MAY add members to a JSON document. Custos MUST ignore members that it does not know.

## OpenAPI

The specification holds no OpenAPI document. The reference server generates one from its code. Where the two disagree, these documents are the authority. The tests of each implementation validate its JSON documents against [schemas/](schemas/).

## Changes

See [CHANGELOG.md](CHANGELOG.md). While the version is `v0`, the contract can change without notice.
