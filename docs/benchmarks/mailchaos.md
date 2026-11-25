# MailChaos: method

MailChaos asks one question: **under injected faults, how many emails does each delivery
design lose, and how many does it send twice?** SMTP has no idempotency key, so "exactly
once" is impossible in general. The goal is to measure how close each design gets.

Run it: `bench/run-mailchaos.sh` (about a minute; needs Docker). `--quick` does a 300-message
smoke run. The script writes `bench/results/mailchaos/results.json` and `mailchaos.png`, rewrites
the README table from that file, and runs `bench/check_mailchaos.py` as a gate.

## Systems compared

All three use the same in-memory provider, the same fault plan and seed, and the same clients.

| System | How it works |
|---|---|
| **naive send-then-save** | The request handler calls the provider, then saves the row. On any error the client retries. |
| **outbox only** | The real Relay engine: transactional accept with an `Idempotency-Key`, outbox, SKIP LOCKED workers and leases. Unknown outcomes are treated as failures and resent at once (`UnknownOutcome = RetryImmediately`). |
| **outbox + unknown state** | The real engine with the Unknown state: a timeout or a crash mid-send becomes Unknown and is not resent blindly. A provider delivery event reconciles it, matched by our deterministic Message-ID. If no event arrives within the 2 s reconcile window, it is resent (`RetryAfterWindow`). |

## Faults

Each provider call draws a fault with probability *f* (the x-axis). The fault type is
uniform over five kinds:

| Fault | What happens |
|---|---|
| CrashBeforeSend | The worker or process dies before calling the provider. It never writes again, and its lease expires. |
| CrashAfterAccept | The provider accepts the mail, then the process dies before recording it. |
| TimeoutBeforeAccept | The caller sees a timeout; the provider never got the mail. |
| TimeoutAfterAccept | The caller sees a timeout; the provider did get the mail. |
| Outage | The provider refuses (definitely not sent). |

In addition:

- **Database restarts:** `round(f × 20)` times per run, at evenly spaced points in the
  submission sequence, every connection to the run's database is killed
  (`pg_terminate_backend`). This happens at the same stage of work for every system.
- **Duplicate webhooks** (system 3 only, since it is the only one that consumes events):
  each delivery event is sent twice with probability *f*. Events arrive 50–150 ms after
  acceptance and are redelivered on any failure, as providers do.

"Process kills" are simulated in-process: an exception aborts the worker immediately, with no
cleanup, and the worker restarts with a new identity. Its claimed rows stay leased until the
1 s lease expires. The effect on the database is the same as a `kill -9`, which is the
part that matters here. Provider timeouts and outages are injected in the provider adapter
rather than through Toxiproxy. Both are deliberate deviations from the plan; see "Limits".

## Measurement

- The **ledger** records every email the provider accepted, keyed by the logical message
  (its subject). It is the ground truth, independent of what any system believes.
- **Lost** = logical messages with zero ledger entries after the run drains.
- **Duplicates** = ledger entries minus distinct messages.
- Both are reported per 10,000 messages. 2,000 messages per run, 8 concurrent clients, 4 workers.
- Each client retries any failed request up to 10 times, using the same `Idempotency-Key` for
  systems 2 and 3.
- A run drains until every message is final or 60 s pass. Anything still open is reported
  as `unresolved`.
- `deliveryLatencyMs` is the time from first submission to first acceptance by the provider.

## Reading the results

- Systems 1 and 2 duplicate at the same rate because the same mechanism drives both:
  every after-accept fault (crash or timeout) looks like a failure, so the mail is sent
  again. The outbox makes acceptance durable, and it would prevent losses if clients
  didn't retry. It does nothing about duplicates.
- System 3 turns those same faults into Unknown and waits for evidence instead of
  guessing. That costs latency: its p99 is higher because Unknown messages wait for an event
  or the reconcile window.
- Losses are zero for all three in this setup, because clients retry and the provider
  always recovers. A naive design with fire-and-forget clients would also lose mail.
  That case is not measured here.

## Limits

- The provider is in-memory, so absolute latencies are not representative. Only the
  comparisons are meaningful.
- Fault draws come from a seeded PRNG, but concurrency changes the draw order, so reruns
  match statistically, not bit for bit.
- Process kills are simulated in-process, not with real `kill -9`. Network faults are
  injected in the adapter, not with Toxiproxy.
- System 3 can still duplicate if a provider event arrives after the reconcile window.
  This run's event delay (≤ 150 ms) is well inside the 2 s window. Real providers can
  be slower, so tune the window to your provider.
