# Versioning

## Contract version

The contract version is one number. The current version is `v0`. In the discovery document it is the string `"0"`.

- The specification has no minor or patch versions.
- While the version is `v0`, any part of the contract can change without notice. Custos and a service MUST use the same revision of `spec/`.
- The Maintainer decides when the contract gets a version after `v0`, and the compatibility rules for it.

## Agreement

1. A service lists each contract version that it supports in `custos_versions_supported` of the discovery document ([01-discovery.md](01-discovery.md)).
2. Custos selects the highest version in that list that it also supports.
3. If no version matches, Custos MUST NOT send data to the service. It reports the mismatch to the user.

## Unstable parts

A document can mark a part as unstable, as a warning that it changes soon. In `v0`, these parts are unstable:

- The upload result document ([03-upload.md](03-upload.md#result-document)).
- The stream ([05-stream.md](05-stream.md)).
