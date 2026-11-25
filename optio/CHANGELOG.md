# Changelog

No release yet. The first release is 0.1.0 ([#48](https://github.com/atlashq-gg/custos/issues/48)).

## Unreleased

- The producer contract [docs/producers.md](docs/producers.md) and its schema [schemas/event.schema.json](schemas/event.schema.json): a domain service publishes each event with its data and its access policy on NATS JetStream.
- The NATS configuration [nats/nats.conf](nats/nats.conf): the users of the producer and of Optio, with their permissions.
