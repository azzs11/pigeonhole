# Relay

[![ci](https://github.com/azzs11/scalable-email-service/actions/workflows/ci.yml/badge.svg)](https://github.com/azzs11/scalable-email-service/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Durable, idempotent email delivery for .NET, with provider failover and an
**Agent Send Guard** that lets AI agents send mail safely.

> **Status: Phase 1 of 8.** Messages are accepted durably into PostgreSQL with an
> outbox row, but are **not sent yet**. Real delivery arrives in Phase 2. Nothing
> below is a measured claim until a benchmark result in `bench/results/` backs it.

## What it is (and is not)

- **Is:** a delivery service that accepts a message, stores it durably, and
  sends it through SMTP or an API provider (MailKit, SES, Resend), with
  retries, failover and bounce-aware suppression.
- **Is:** a policy layer and MCP server so AI agents can send mail only
  inside rules you set (allowed recipients, caps, dedupe, human approval).
- **Is not:** a mail server. It does not replace Postfix, receive inbound
  mail, or run marketing campaigns.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in
`global.json`) and Docker.

```bash
docker compose -f deploy/docker-compose.yml up -d           # PostgreSQL on :5433
dotnet run --project tools/Relay.Cli -- keys create --tenant demo   # prints a key once
dotnet run --project src/Relay.Api                           # http://localhost:5238, docs at /docs

curl -H "Authorization: Bearer <key>" -H "Idempotency-Key: demo-1" \
     -H "Content-Type: application/json" \
     -d '{"to":["r@example.test"],"subject":"Hi","text":"Hello"}' \
     http://localhost:5238/v1/messages
```

In Development the API applies migrations on startup. Elsewhere, run
`relay db migrate`. More requests are in
[`src/Relay.Api/Relay.Api.http`](src/Relay.Api/Relay.Api.http). Design decisions are in
[`docs/adr/`](docs/adr/).

```bash
dotnet test   # unit, architecture, and integration tests (integration needs Docker)
```

## Layout

| Project | Role |
|---|---|
| `Relay.Domain` | Entities and the delivery state machine. References nothing. |
| `Relay.Application` | Use cases and ports. Depends only on Domain. |
| `Relay.Infrastructure` | Persistence, outbox, provider adapters (Phases 1–2). |
| `Relay.Api` | HTTP API. |
| `Relay.Worker` | Outbox relay and schedulers (Phase 2). |
| `Relay.Guard` | Agent Send Guard policy engine (Phase 5). |
| `Relay.Mcp` | MCP server exposing the Guard (Phase 5). |
| `tools/Relay.Cli` | Operator CLI: migrations, create and revoke API keys. |

Dependencies point inward. `tests/Relay.ArchitectureTests` fails the build if
they don't.

## Roadmap

0. Clean repo, .NET 10, CI
1. Durable core (PostgreSQL, transactional accept, idempotency keys) and security fixes ← *you are here*
2. Delivery engine: outbox relay, providers, retries, failover
3. Webhooks, suppression, atomic quotas
4. Observability and **MailChaos**, a failure-injection benchmark
5. Agent Send Guard, MCP server and **AgentMailBench**
6. Dashboard
7. Docs, one-command demo, v1.0.0

## License

[MIT](LICENSE)
