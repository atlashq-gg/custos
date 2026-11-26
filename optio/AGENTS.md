# Agents: Optio

- Optio conforms to [spec/](../spec/README.md). Where the code and spec/ disagree, change the code, or change spec/ in the same pull request.
- Keep the JSON members in `snake_case`. The tests validate the documents against `spec/schemas/`.
- Use minimal APIs. Keep one file for each group of endpoints in `Optio/Endpoints/`.
- Run `dotnet csharpier format .` before a commit. Keep each line at 100 characters or less, comments and strings too: CSharpier does not wrap them. CI checks both.
- Run `dotnet test` before a commit. Tests that start containers need Docker.
- Optio holds no records of a domain service. Its database holds only its own settings for each guild. It calls a domain service only with a token of the user for that service ([ADR 0006](../docs/adr/0006-optio-is-the-service-of-the-contract.md)).
- A domain service controls the access to its own APIs. Optio controls the access to the stream: it applies the policy of each event ([ADR 0007](../docs/adr/0007-a-broker-carries-domain-events.md), [docs/producers.md](docs/producers.md)).
- Change the database only through an EF Core migration in `Optio/Storage/Migrations/`.
- Keep `nats/nats.conf` in step with [docs/producers.md](docs/producers.md). The tests use the same file.
- Keep the state of a query in the buckets of the broker, not in memory. Each change of state is a compare-and-set on the record of the query, and only the winner publishes its message.
- Optio has its own Discord bot token. It uses the bot to read the roles of the users of the stream, and for `/optioadmin`. Each command handler needs a check of the permission of the user.
- Never write a token, a client secret, or a model key to a log.
- Read a secret from the environment or from a file in `/run/secrets`. Examine the options when the host starts.
- Only the tests in the category `Evaluation` call a model provider, with the key in `Optio__ClaudeApiKey`. Without it, they skip. CI does not run them. Run them by hand when a card, the system prompt, the operator text, the tool list of a service, or the model changes.
- The tests in the categories `Feasibility` and `Discord` do not run in CI. Run them by hand when the broker, its client, or the Discord client changes.
- The content of this folder is under AGPL-3.0-or-later.
