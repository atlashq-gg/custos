# Versioning

## Contract version

The contract version has the form `MAJOR.MINOR`, for example `0.1`.

- A change that can break a compliant client or a compliant service increments MAJOR. Examples: a member that changes its type, a new required member, a removed endpoint.
- A change that cannot break either side increments MINOR. Examples: a new optional member, a new optional endpoint.
- While MAJOR is `0`, a MINOR change can break a client. Custos and a service MUST then match on the full version.

## Agreement

1. A service lists each contract version that it supports in `custos_versions_supported` of the discovery document ([discovery.md](discovery.md)).
2. Custos selects the highest version in that list that it also supports.
3. If no version matches, Custos MUST NOT send data to the service. It reports the mismatch to the user.

## Unstable parts

A document can mark a part as unstable. An unstable part can change in a MINOR version, also after MAJOR `1`. In contract version `0.1`, the upload result document is unstable ([upload.md](upload.md#result-document)).

## Project version

The `spec/` project has its own version, `MAJOR.MINOR.PATCH`. A PATCH change corrects text and does not change the contract version.
