# Agents: optio

- `optio` conforms to [spec/](../spec/README.md). Where the code and spec/ disagree, change the code, or change spec/ in the same pull request.
- Keep the JSON members in `snake_case`. The tests validate the documents against `spec/schemas/`.
- Use minimal APIs. Keep one file for each group of endpoints in `Optio/Endpoints/`.
- Run `dotnet csharpier format .` before a commit. Keep each line at 100 characters or less, comments and strings too: CSharpier does not wrap them. CI checks both.
- Run `dotnet test` before a commit. Tests that start containers need Docker.
- `optio` holds no records of a domain service. Its database holds only its own settings for each guild. It calls a domain service only with a token of the user for that service ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)).
- Change the database only through an EF Core migration in `Optio/Storage/Migrations/`.
- A domain service controls the access to its own APIs. `optio` controls the access to the stream: it applies the policy of each event ([ADR 0007](../docs/adr/0007-a-broker-carries-domain-events.md), [docs/producers.md](docs/producers.md)).
- `optio` has its own Discord bot token. It uses the bot to read the roles of the users of the stream, and for `/optioadmin`. Each command needs a check of the permission of the user.
- The harness calls the model only through `IModelClient`, so that another provider needs only one more implementation. The model sees no token.
- Never write a token, a client secret, or a model key to a log.
- Read a secret from the environment or from a file in `/run/secrets`. Examine the options when the host starts.
- No automated test calls a model provider.
- The content of this folder is under AGPL-3.0-or-later.
