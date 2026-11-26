# Agents: server

- The server conforms to [spec/](../spec/README.md). Where the code and spec/ disagree, change the code, or change spec/ in the same pull request.
- Keep the JSON members in `snake_case`. The tests validate the documents against `spec/schemas/`.
- Use minimal APIs. Keep one file for each group of endpoints in `Custos.Server/Endpoints/`.
- Run `dotnet csharpier format .` before a commit. Keep each line at 100 characters or less, comments and strings too: CSharpier does not wrap them. CI checks both.
- Run `dotnet test` before a commit. All tests need Docker for PostgreSQL. The test in the category `Integration` also starts Keycloak.
- Change the database only through an EF Core migration in `Custos.Server/Storage/Migrations/`. Write the DDL of a partitioned history table with SQL in the migration.
- Keep the realm in [deploy/keycloak/custos-realm.json](../deploy/keycloak/custos-realm.json) in step with the tests `KeycloakLoginTests` and `TokenExchangeTests`. The tests read the same file.
- Keep the events in step with the producer contract in `optio/docs/producers.md`. The tests validate them against `optio/schemas/`.
- A new or changed Discord command or MCP tool needs its line in `Custos.Server.Tests/Discord/CommandGuards.txt` or `Custos.Server.Tests/Mcp/McpTools.txt`. Each command and each tool needs a guard.
- The automated tests run without Discord: they replace `IDiscordDirectory` with `FakeDiscordDirectory`.
- The content of this folder is under AGPL-3.0-or-later, except `catalog/catalog.json`: it is under CC BY-NC-SA 4.0. Change the file only with `catalog/update-catalog.sh`, and keep the date in `catalog/LICENSE.md` correct.
