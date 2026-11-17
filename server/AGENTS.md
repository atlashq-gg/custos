# Agents: server

- The server conforms to [spec/](../spec/README.md). Where the code and spec/ disagree, change the code, or change spec/ in the same pull request.
- Keep the JSON members in `snake_case`. The tests validate the documents against `spec/schemas/`.
- Use minimal APIs. Keep one file for each group of endpoints in `Custos.Server/Endpoints/`.
- Run `dotnet test` before a commit. The test in the category `Integration` needs Docker.
- Keep the realm in `Custos.Server.AppHost/keycloak/custos-realm.json` in step with the test `KeycloakLoginTests`. Both use the same file.
- The content of this folder is under AGPL-3.0-or-later.
