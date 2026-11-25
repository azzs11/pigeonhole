# 0001. Transactional outbox in PostgreSQL

- Status: accepted
- Date: 2026-11-25

## Context

A message must never be lost between "the API said 202" and "a worker sends it".
Writing the message and then publishing to a queue in two steps can lose or
duplicate work if the process dies between them.

## Decision

`POST /v1/messages` writes the `messages` row, its first `message_events` row and an
`outbox` row in one `SaveChanges`, which is one PostgreSQL transaction. The Phase 2
worker claims outbox rows with `FOR UPDATE SKIP LOCKED` and a lease
(`locked_by`, `locked_until`). A partial index on `available_at WHERE processed_at IS NULL`
keeps claiming cheap.

We use our own outbox table instead of Wolverine. It is small enough to explain and
benchmark in MailChaos. MassTransit is excluded because v9 needs a commercial licence.

## Consequences

- An accepted message is durable as soon as the API responds.
- One database is the only moving part until Phase 2. There is no broker to run.
- Throughput is bounded by PostgreSQL. Phase 4 measures how far that goes.
