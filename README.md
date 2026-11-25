# Relay

[![ci](https://github.com/azzs11/scalable-email-service/actions/workflows/ci.yml/badge.svg)](https://github.com/azzs11/scalable-email-service/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Durable, idempotent email delivery for .NET, with provider failover and an
**Agent Send Guard** that lets AI agents send mail safely.

> **Status: Phase 4 of 8.** Durable accept, delivery through SMTP, Resend or SES with
> failover, signed provider webhooks, suppression, atomic quotas, a reputation pause,
> OpenTelemetry traces and metrics, and the MailChaos benchmark below. Nothing
> below is a measured claim until a benchmark result in `bench/results/` backs it.

## Headline results: MailChaos

MailChaos kills workers mid-send, times out the provider after (and before) it accepts,
cuts the provider off, restarts the database and duplicates webhooks, then counts
what was lost or sent twice. Method: [docs/benchmarks/mailchaos.md](docs/benchmarks/mailchaos.md).
This table is generated from `bench/results/mailchaos/results.json` by `bench/run-mailchaos.sh`.

<!-- mailchaos:start -->
Duplicates / lost per 10,000 messages (2,000 per run, seed 42, commit `766c6ff-dirty`, generated 2026-11-25). Fault rate is per provider call.

| System | 0% faults | 1% faults | 2% faults | 5% faults | 10% faults |
|---|---|---|---|---|---|
| naive send-then-save | 0 / 0 | 55 / 0 | 105 / 0 | 310 / 0 | 485 / 0 |
| outbox only | 0 / 0 | 55 / 0 | 105 / 0 | 305 / 0 | 495 / 0 |
| outbox + unknown state | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |

![MailChaos results](bench/results/mailchaos/mailchaos.png)
<!-- mailchaos:end -->

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
docker compose -f deploy/docker-compose.yml up -d           # PostgreSQL :5433, Mailpit :1025 / UI :8025
dotnet run --project tools/Relay.Cli -- keys create --tenant demo   # prints a key once
dotnet run --project src/Relay.Api                           # http://localhost:5238, docs at /docs
dotnet run --project src/Relay.Worker                        # delivers to Mailpit; open http://localhost:8025

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

Observability (optional): `docker compose -f deploy/docker-compose.yml --profile observability up -d`
starts Prometheus (:9090), Jaeger (:16686) and Grafana (:3001, "Relay overview" dashboard).
`GET /v1/messages/{id}/explain` describes every state change in plain language.

## Layout

| Project | Role |
|---|---|
| `Relay.Domain` | Entities and the delivery state machine. References nothing. |
| `Relay.Application` | Use cases and ports. Depends only on Domain. |
| `Relay.Infrastructure` | EF Core + PostgreSQL, outbox, SMTP/Resend/SES adapters, resilience. |
| `Relay.Api` | HTTP API. |
| `Relay.Worker` | Outbox relay: claims with SKIP LOCKED, sends, records outcomes. Run as many as you like. |
| `Relay.Guard` | Agent Send Guard policy engine (Phase 5). |
| `Relay.Mcp` | MCP server exposing the Guard (Phase 5). |
| `tools/Relay.Cli` | Operator CLI: migrations, create and revoke API keys. |

Dependencies point inward. `tests/Relay.ArchitectureTests` fails the build if
they don't.

## Roadmap

0. Clean repo, .NET 10, CI
1. Durable core (PostgreSQL, transactional accept, idempotency keys) and security fixes
2. Delivery engine: outbox relay, providers, retries, failover
3. Webhooks, suppression, atomic quotas
4. Observability and **MailChaos**, a failure-injection benchmark ← *you are here*
5. Agent Send Guard, MCP server and **AgentMailBench**
6. Dashboard
7. Docs, one-command demo, v1.0.0

## License

[MIT](LICENSE)
