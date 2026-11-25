# 0005. Provider events, suppression, quotas and the reputation guard

- Status: accepted
- Date: 2026-11-25

## Decision

**Webhooks.** `/v1/webhooks/resend` verifies the Svix HMAC-SHA256 signature over
`{id}.{timestamp}.{body}` and refuses timestamps outside ±5 minutes.
`/v1/webhooks/ses` accepts SNS messages only from allow-listed topics. It verifies
the RSA signature (SignatureVersion 1 or 2) with a certificate fetched only from
`https://sns.<region>.amazonaws.com`, and refuses messages older than 1 hour. Signed
SNS subscription confirmations are confirmed automatically.

**Exactly-once effects.** Each event inserts a `provider_events` row, unique on
`(provider, event_id)`. That insert, the message transitions, the outbox changes and
the suppressions commit in one transaction. A replayed event hits the unique index, so
the whole transaction rolls back and the response is `200 Duplicate`. If the target
message is still in `Sending`, the response is 503 so the provider redelivers later.

**Matching.** Events match messages by provider message id. Failing that, they match
by our deterministic Message-ID (SES `commonHeaders.messageId`). An event for an
**Unknown** message reconciles it to Sent and completes its outbox row, so the message
is never resent.

**Effects.** Delivered moves Sent → Delivered. A hard bounce moves the message to
Bounced and suppresses the recipients. A soft bounce moves Sent back to Accepted and
reopens the outbox row with backoff (Bounced once attempts are exhausted). A
complaint adds a timeline note and suppresses the recipients.

**Suppression** is per tenant on the normalized (lower-case) address. It is checked at
accept time, which returns a typed 422 listing the addresses and accepts nothing, and
again in the worker just before sending, which moves the message to Rejected without
calling any provider. Tenants can list and remove suppressions through `/v1/suppressions`.

**Quota** is a daily count of recipients per tenant, consumed by one SQL statement in
the same transaction as the message insert:
`UPDATE tenants SET quota_used = … WHERE … used + n <= daily_quota RETURNING …`.
It resets at 00:00 UTC. Every recipient costs one unit, including bulk recipients and
cc/bcc. Concurrent requests serialize on the tenant row. An idempotent replay costs
nothing, and losing a race on the same key rolls back the loser's units.

**Request rate** uses the built-in ASP.NET Core rate limiter: a token bucket per API
key (default 50/s, burst 100) and per IP for anonymous requests. Rejections return
429 ProblemDetails with `Retry-After`.

**Reputation guard.** After every hard bounce or complaint, the tenant's rates over
the trailing 24 h are computed, once at least 100 messages were sent in that window.
If bounces exceed 5% or complaints exceed 0.1%, sending is paused. New sends then get
403 with the reason, and queued messages wait without using up attempts.
`relay tenants resume` lifts the pause.

## Consequences

- Webhook endpoints don't need API keys. Their trust comes from provider signatures.
- The tenant row is a serialization point for sends. That is fine at the target load
  (500 req/s); Phase 4 measures it.
