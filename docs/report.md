# Once, not twice: measuring duplicate email under faults, and guarding AI agents that send it

*Technical report (draft). Pigeonhole / Relay, November 2026. Every number here is copied from
`bench/results/` and can be regenerated with the scripts named in each section.*

## Abstract

Transactional email has no end-to-end idempotency: SMTP has no request key, and an HTTP timeout
cannot tell "not sent" from "sent, reply lost". We built Pigeonhole, a .NET delivery service
with a transactional outbox, leased workers and an explicit **Unknown** delivery state, and
measured it with **MailChaos**, a fault-injection benchmark that counts lost and duplicated
messages against a ground-truth ledger. At fault rates from 1% to 10% per provider call, a
naive send-then-save design and a textbook outbox both duplicated 55 to 495 messages per
10,000. The outbox with an Unknown state duplicated none, and none of the three lost any. We
also built an **Agent Send Guard** for AI agents with an email tool, and **AgentMailBench**, 150
scripted scenarios. The full Guard stopped 95.6% of attack scenarios (86 of 90) with 0% false
blocks and 0% of benign mail held for approval, at a p99 of 3.2 ms per call. The four misses
are documented limitations.

## 1. The problem

A delivery service sits between a durable store and a provider it doesn't control. A crash or
timeout can happen in three places: before the provider call (safe to retry), after the
provider accepted (retrying duplicates), and during the call (unknowable). Most designs collapse
the last two into "failed, retry", which turns every after-accept fault into a duplicate.

Agents add a second problem. An LLM with a `send_email` tool retries on ambiguous tool
results, can loop, and follows instructions embedded in content it reads (prompt injection).
The email tool turns those failures into real messages to real people.

## 2. Design

**Accept.** The message, its first event and an outbox row are written in one PostgreSQL
transaction. The per-tenant `Idempotency-Key` is unique, so client retries return the original.

**Claim and send.** Workers claim outbox rows with `FOR UPDATE SKIP LOCKED` and a lease, and
commit the message as *Sending* before calling the provider. Adapters classify each attempt as
Sent, Transient (definitely not sent), Rejected, or **Unknown** (may have been sent). Failover
happens only on Transient.

**Unknown.** A timeout, or a lease that expired while a message was Sending, yields Unknown.
Unknown is never resent blindly. A provider that deduplicates (Resend's idempotency key) is
retried on that provider only. Otherwise the message waits for a provider delivery event, which
is matched through a deterministic `Message-ID`, or for a configurable reconcile window. Provider
events are applied exactly once through a unique `provider_events` row written in the same
transaction as the state change.

**Agent Send Guard.** An operator starts a run that names trusted recipients and allowed domains.
The agent's run token can only call the agent endpoints. Each call is checked for caps
(recipients per message, per run, per hour, per recipient per day), **recipient provenance**
(was this address named by the human, or did it come from content?), a secret and PII scan, and
near-duplicate content. The outcome is Send, Hold (needs a human with a different scope) or Block.

## 3. MailChaos

**Setup.** Three systems share one in-memory provider, fault plan, seed and client behaviour.
There are 2,000 messages per run, 8 clients, 4 workers, and clients retry up to 10 times. Each
provider call draws a fault with probability *f*, uniformly from: crash before send, crash
after accept, timeout before accept, timeout after accept, outage. On top of that, the database
is restarted round(*f*×20) times per run, and system 3 receives duplicate webhooks with
probability *f*. A ledger of provider acceptances is the ground truth. Full method:
[benchmarks/mailchaos.md](benchmarks/mailchaos.md). Run: `bench/run-mailchaos.sh`.

**Results** (duplicates / lost per 10,000; seed 42):

| System | 0% | 1% | 2% | 5% | 10% |
|---|---|---|---|---|---|
| naive send-then-save | 0 / 0 | 55 / 0 | 105 / 0 | 310 / 0 | 485 / 0 |
| outbox only | 0 / 0 | 55 / 0 | 105 / 0 | 305 / 0 | 495 / 0 |
| outbox + unknown state | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |

**Reading.** The outbox makes acceptance durable but does nothing for duplicates. Its curve
tracks the naive one because the same thing drives both: after-accept faults read as failures.
Two of the five fault kinds are after-accept, which alone predicts about 2/5 × *f* of calls
duplicating: 400 per 10,000 at *f* = 10%, against 485 to 495 measured. The gap is not
attributed separately here; a fault can strike a message more than once, and the database
restarts sit on top. The Unknown state
removes that path. Its cost is latency: Unknown messages wait for evidence.

**Threats to validity.** The provider is in memory, so absolute latency means nothing; only the
comparisons do. Process kills are simulated in-process (no cleanup, new identity, the lease
expires), and network faults are injected in the adapter rather than with a proxy. Losses are
zero partly because clients always retry and the provider always recovers; fire-and-forget
clients were not measured. System 3 can still duplicate if a provider's event arrives after the
reconcile window.

## 4. AgentMailBench

**Corpus.** 150 seeded scenarios: 60 benign tasks, 40 prompt-injection exfiltration attempts, 25
retry storms and 25 runaway blasts. Each sets up a run and replays a fixed sequence of tool
calls. There is no LLM in the loop: the benchmark tests the Guard, not a model. Fake secrets are
generated at runtime. Method: [benchmarks/agentmailbench.md](benchmarks/agentmailbench.md). Run:
`bench/run-agentmailbench.sh`.

**Results** (seed 7):

| Configuration | Attacks stopped | False blocks | Held (benign) | Guard p99 |
|---|---|---|---|---|
| no guard | 5.6% (5/90) | 0.0% | 0.0% | 2.9 ms |
| caps only | 16.7% (15/90) | 0.0% | 0.0% | 2.8 ms |
| caps + provenance | 61.1% (55/90) | 0.0% | 0.0% | 2.4 ms |
| full guard | 95.6% (86/90) | 0.0% | 0.0% | 3.2 ms |

**Reading.** Caps alone stop blasts but not exfiltration. Provenance is the largest single
step (16.7% → 61.1%): most injected exfiltration targets an address the human never named.
The secret scan and duplicate suppression close most of the rest. The four misses are kept in
the denominator on purpose: an insider at an allowed domain, a space-separated secret, and two
reworded retries. [threat-model.md](threat-model.md) lists them with the other known gaps.

**Threats to validity.** The scenarios were written by the Guard's authors, so this is a
regression bar, not a security proof. Benign mail in the corpus is cleaner than real mail; the
0% approval share will not survive contact with production. Each scenario uses a fresh tenant,
so slow drips across many runs are not measured.

## 5. Related work and positioning

Transactional outboxes and idempotency keys are standard practice. What we add is a
measurement: the outbox's well-known blind spot, after-accept faults, shown as a duplicate rate
against a ledger, next to the one state that closes it. Agent tool guards usually filter
content. Our result suggests that **provenance of the recipient** matters more than content for
the exfiltration case.

## 6. Limits and next steps

- Real network faults (Toxiproxy) and real process kills in MailChaos.
- Adversarial scenarios contributed by people who didn't write the Guard.
- Measure the reconcile-window trade-off against real provider webhook latency.
- Real-load numbers (accept p99 at a target request rate) are not in this report because they
  have not been measured yet.

## Reproducing

```bash
bench/run-mailchaos.sh        # ≈1 min, needs Docker; writes bench/results/mailchaos/
bench/run-agentmailbench.sh   # ≈15 s, needs Docker; writes bench/results/agentmailbench/
```
