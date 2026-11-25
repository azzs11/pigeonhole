# 0004. Delivery outcomes, failover and the Unknown state

- Status: accepted
- Date: 2026-11-25

## Context

SMTP has no idempotency key. If a worker loses the connection after handing over the
message, or crashes before recording the result, we can't know whether the email went
out. A blind retry can send it twice. Never retrying can lose it.

## Decision

- Every provider adapter classifies each attempt as **Sent**, **Rejected** (definitely
  not sent, and retrying won't help), **Transient** (definitely not sent, safe to retry
  or fail over), or **Unknown** (may have been sent). Adapters don't throw for delivery
  failures.
- **Failover happens only on Transient.** Timeouts are Unknown, because the provider may
  have the message, so they never fail over.
- Each provider has its own resilience pipeline (Microsoft.Extensions.Resilience / Polly v8):
  a retry for Transient results (exponential backoff with jitter), then a circuit breaker,
  then a timeout. Across attempts, the outbox row is rescheduled with exponential backoff
  and ±20% jitter. After `MaxAttempts` the message is dead-lettered.
- The worker commits **Sending** before calling the provider. If a lease expires while a
  message is still in Sending, the worker that reclaims it marks the message **Unknown**
  and does not resend.
- Unknown on a provider that deduplicates (Resend's Idempotency-Key) is retried on
  **that provider only**, by pinning it on the outbox row. Otherwise the configurable
  `UnknownOutcome` policy applies: `Hold` (the default; wait for provider events in
  Phase 3 or an operator) or `RetryAfterWindow` (resend after the reconcile window,
  accepting a possible duplicate).
- Every message has a deterministic `Message-ID` (`<{id}@{domain}>`) that stays the same
  across attempts. Phase 3 uses it to reconcile provider events with messages.
- Claiming uses `FOR UPDATE SKIP LOCKED` with a lease. The outbox's `locked_by` and the
  message's `xmin` are optimistic concurrency tokens, so a worker that lost its lease
  can't overwrite the new owner's work.

## Consequences

- No silent duplicates from crashes or timeouts. The cost is some messages parked in
  Unknown until Phase 3's reconciliation.
- MailChaos (Phase 4) can compare three systems directly: naive send-then-save, the outbox
  without Unknown, and the outbox with Unknown.
