# 0003. Idempotency-Key semantics

- Status: accepted
- Date: 2026-11-25

## Context

Clients and AI agents retry. Without idempotency, a retry after a timeout sends the
same email twice.

## Decision

- `Idempotency-Key` is optional, 1–255 printable ASCII characters, and unique per tenant.
  A unique index on `(tenant_id, idempotency_key)` enforces this.
- The request is normalised (default sender applied) and hashed with SHA-256. The hash
  is stored with the message.
- The same key with the same request returns the original message: `202`, the same id,
  and an `Idempotent-Replayed: true` header.
- The same key with a different request returns `422 Unprocessable Entity`. This follows
  the IETF idempotency-key draft.
- Races: if two requests insert at once, the unique index rejects the loser. It then
  re-reads the winner and replays or conflicts as above, so exactly one row is written.
- Keys don't expire yet. A retention window will come with the Phase 3 cleanup jobs.

## Consequences

- Retries are safe end to end at the API boundary. Delivery-level exactly-once is a
  separate problem, handled by the Phase 2 Unknown state.
