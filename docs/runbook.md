# Runbook

What to do when the sorting office misbehaves. Commands assume the repo root. `relay` is short
for `dotnet run --project tools/Relay.Cli --`, which uses `ConnectionStrings:Relay` from its
appsettings or the environment.

## Start, stop, reset

| Task | Command |
|---|---|
| Whole stack (sandbox) | `docker compose up -d` → dashboard :5173, Mailpit :8025 |
| Is it alive? | `deploy/smoke.sh` (demo key → one send → exactly one letter in Mailpit) |
| Logs | `docker compose logs -f api worker` |
| More workers | `docker compose up -d --scale worker=3` (safe: claims use SKIP LOCKED) |
| Wipe all data | `docker compose down -v` (removes the PostgreSQL volume) |
| Migrations | `relay db migrate` (the API also migrates on start when `Database:MigrateOnStartup` is true) |

## Keys and tenants

```bash
relay keys create --tenant acme             # prints the key once; only a hash is stored
relay keys revoke --prefix rly_ab12cd34     # rotate: create the new one first, then revoke
relay tenants quota --tenant acme --daily 5000
relay tenants pause  --tenant acme          # stops accepting; queued mail waits
relay tenants resume --tenant acme
relay runs create --tenant acme --trusted maya@example.test   # agent run token + MCP command
```

## Symptoms

**Messages pile up in the outbox** (Overview "Waiting in outbox" climbs; `relay_outbox_depth`).

1. Providers page: is a circuit **Tripped**? The breaker reopens on its own; check the provider's
   status page and the worker logs for the failure detail.
2. Are workers running? `docker compose ps worker`. Rows locked by a dead worker are picked up
   again when their lease runs out (`Relay:Delivery:LeaseSeconds`, default 120).
3. Is the tenant paused? Settings shows the reason. `ReputationGuard` pauses on a bounce rate
   above 5% or a complaint rate above 0.1% (over 24 h, with at least 100 sends). Fix the list,
   then `relay tenants resume`.

**Messages in Unknown.** This is expected after timeouts or worker crashes. It means "may have
been sent", and Pigeonhole will not guess.

- With `UnknownOutcome = Hold` (the default) they wait for a provider webhook. Make sure webhooks
  are configured (`Relay:Webhooks`). A delivered/bounced event moves them on.
- To resend after a quiet period instead, set `Relay:Delivery:UnknownOutcome=RetryAfterWindow`
  and `ReconcileWindowSeconds` to longer than your provider's slowest webhook. This accepts a
  possible duplicate.
- `GET /v1/messages/{id}/explain` says which attempt went Unknown and why.

**Dead letters.** `MaxAttempts` (6) ran out, or the provider rejected the message outright. The
timeline has the provider's reason. Fix the cause and send again with a **new** `Idempotency-Key`.

**Agent mail held.** Approvals page, or `GET /v1/approvals`. The card says which rule fired.
Pending approvals expire after 60 minutes and their messages become Rejected.

**429 Too Many Requests.** Per-key and per-IP limits (`Relay:RateLimit`). The sandbox demo key
endpoint allows 5 per IP per hour. If every visitor shares one bucket behind a proxy, set
`Relay:Proxy:KnownNetworks` to the proxy's network so `X-Forwarded-For` is trusted.

**Webhook 401s.** Wrong signing secret (Resend), or an SNS topic not listed (SES). Signatures
older than `ToleranceSeconds` (300) are refused on purpose. Duplicated webhooks are harmless:
each provider event is applied once.

## Configuration you will touch

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:Relay` | (none) | PostgreSQL |
| `Relay:Providers:N:{Name,Type,…}` | (none) | Failover order. Type: `Smtp`, `Resend`, `Ses`. Secrets via environment only |
| `Relay:Delivery:MaxAttempts` / `LeaseSeconds` | 6 / 120 | |
| `Relay:Delivery:UnknownOutcome` | `Hold` | or `RetryAfterWindow` |
| `Relay:Messaging:DefaultFrom` | (none) | |
| `Relay:Sandbox:Enabled` | false (true in Development) | Never on a host with real provider credentials |
| `Relay:Proxy:KnownNetworks:N` | (none) | CIDRs whose `X-Forwarded-For` is trusted |
| `Relay:Telemetry:Otlp*Endpoint` | (none) | OTLP traces and metrics |

## Public sandbox checklist

- `Relay__Sandbox__Enabled=true` on the API **and** the worker. The worker then refuses SES and Resend.
- The only provider is SMTP to Mailpit, and no provider credentials exist on the host.
- Publish only the dashboard (nginx). Keep the API port private so `X-Forwarded-For` comes only from nginx.
- Run `deploy/smoke.sh https://your-host https://your-host-mailpit` after every deploy.
