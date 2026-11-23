# Custos

Custos is a tray application for Foxhole. It watches the map data save file of the game for changes and sends it to a remote server that conforms to the Custos specification. Custos gets its configuration and authentication context from a compliant service through a discovery mechanism.

This repository holds Custos, the specification, `optio` (the service that Custos calls), and the server of the guild records.

## Layout

Each project is a folder at the root ([ADR 0002](docs/adr/0002-monorepo-with-projects-at-the-root.md)).

| Folder | Purpose | Stack | License |
|---|---|---|---|
| `spec/` | The contract between Custos and a server, including discovery and authentication. | Language-neutral | MIT |
| `custos/` | The tray application. | Rust, Tauri | MIT |
| `optio/` | The service that Custos calls. It implements the specification and relays to the domain services. | .NET 10 | AGPL-3.0-or-later |
| `server/` | The domain service of the guild records, behind `optio`. | .NET 10 | AGPL-3.0-or-later |
| `docs/` | Documents and decisions that cross project lines. | | MIT |

Each project folder has a `README.md`, an `AGENTS.md`, a `CHANGELOG.md`, and a `LICENSE`.

## Documents

- [GOVERNANCE.md](GOVERNANCE.md): roles, decisions, discussions, and the change process.
- [AGENTS.md](AGENTS.md): the minimum an agent reads before work.
- [SECURITY.md](SECURITY.md): how to report a vulnerability.
- [docs/](docs/README.md): cross-project documents and the ADR index.

## License

The content of this repository is under the MIT license ([LICENSE](LICENSE)), except `optio/` and `server/`, which are under the AGPL-3.0-or-later license in their own `LICENSE` files ([ADR 0005](docs/adr/0005-license-by-project.md)). The catalog of the game in `server/catalog/` is a file from the Foxhole wiki under CC BY-NC-SA 4.0 ([server/catalog/LICENSE.md](server/catalog/LICENSE.md)). The icons of Custos are third-party files under CC BY-NC 4.0 ([custos/crates/custos-app/icons/LICENSE.md](custos/crates/custos-app/icons/LICENSE.md)).
