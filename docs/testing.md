# Testing report

What was tested, how, and what broke along the way. Every number here comes from a file in
`bench/results/` or from the command shown next to it. Runs were on one laptop (macOS, 10 cores,
Docker Desktop), on 2026-11-25 and 2026-11-26.

| Test | Result | Source |
|---|---|---|
| Unit, architecture and integration tests | all passing | `dotnet test` |
| Property-based tests | 7 invariants, 2,000 generated cases each, all hold | `tests/Relay.UnitTests/PropertyTests.cs` |
| Line coverage | 85.6% of `src/` | `bench/results/coverage/summary.json` |
| MailChaos, 5 seeds × 10,000 messages | 0 lost, 0 duplicates for the outbox + Unknown design | `bench/results/mailchaos-seeds/` |
| Real crash test (`kill -9`) | 0 lost, 0 duplicates across 23,000 messages and 129 kills | `bench/results/crash/` |
| Load test, accept path | after three fixes, 500 req/s went from mostly failing to clean (single run on a busy laptop); repeat runs drifted more than 10×, so no ceiling is claimed | `bench/results/load/` |
| Mutation testing | Domain 34.0% → 55.3%, Guard 37.9% → 49.7% (88.3% of covered code), Application 53.1% → 66.0% | `bench/results/mutation/summary.json` |

## 1. Property-based tests

The example-based tests check the cases someone thought of. FsCheck generates thousands of
inputs and shrinks any failure down to a minimal one. Each property is an invariant the system
relies on:

| Property | Why it matters |
|---|---|
| Any sequence of transition attempts keeps the state machine consistent: refused transitions change nothing, nothing leaves a terminal state, the event log is gapless and chained | Every status a client sees comes from this log. |
| `Sent` is reachable only from `Sending` or `Unknown`, and "terminal" means "no outgoing edge" | Nothing is marked sent without a worker holding it. |
| Backoff stays within ±20% of the capped exponential for any attempt, base, cap and jitter | Retries never hammer a provider or wait forever. |
| The content fingerprint ignores case, spacing, digits and punctuation, but changes when a letter does | The Guard's near-duplicate check can't be dodged by reformatting. |
| The secret scanner finds every Luhn-valid 16-digit card number and never throws, even on arbitrary UTF-16 with lone surrogates | Agent bodies are untrusted input. |
| Any control character in a subject is rejected | Header injection. |
| CR, LF or NUL anywhere in an address is rejected | Header injection through `To`. |

## 2. Coverage

`bench/coverage.sh` runs the unit and integration suites with coverlet, then merges the reports
(a line counts as covered if any suite hits it). EF migrations and generated code are excluded.

| Project | Lines covered |
|---|---|
| Relay.Domain | 93.1% (175/188) |
| Relay.Application | 91.5% (346/378) |
| Relay.Guard | 90.5% (428/473) |
| Relay.Api | 85.0% (384/452) |
| Relay.Infrastructure | 82.9% (1190/1435) |
| Relay.Mcp | 36.8% (14/38) |
| **All** | **85.6% (2537/2964)** |

The files with the most uncovered lines are `Sandbox/Sandbox.cs` (58 of 160), `AgentGuardService.cs`
(42 of 171), `EmailMaskingEnricher.cs` (27 of 46), `AgentEndpoints.cs` (27 of 77) and
`SmtpEmailProvider.cs` (26 of 55). `Relay.Worker` and `Relay.Cli` are thin hosts and are not
measured.

## 3. MailChaos across seeds

The README headline is one run (seed 42). To check that it isn't one lucky draw, the same
benchmark ran on five more seeds at 10,000 messages per run (`--messages 10000 --rates 0.05,0.1
--seed N`). Duplicates / lost per 10,000:

| Seed | naive, 5% | outbox only, 5% | outbox + Unknown, 5% | naive, 10% | outbox only, 10% | outbox + Unknown, 10% |
|---|---|---|---|---|---|---|
| 1 | 216 / 0 | 213 / 0 | 0 / 0 | 440 / 0 | 435 / 0 | 0 / 0 |
| 2 | 198 / 0 | 194 / 0 | 0 / 0 | 441 / 0 | 438 / 0 | 0 / 0 |
| 3 | 218 / 0 | 211 / 0 | 0 / 0 | 447 / 0 | 443 / 0 | 0 / 0 |
| 4 | 228 / 0 | 224 / 0 | 0 / 0 | 443 / 0 | 438 / 0 | 0 / 0 |
| 5 | 241 / 0 | 238 / 0 | 0 / 0 | 493 / 0 | 489 / 0 | 0 / 0 |

Webhook processing lag for the Unknown design stayed under 30 ms at p99 in every run.

### A bug this found in the benchmark

The first multi-seed runs did *not* show zero. At 10,000 messages the Unknown design sent
280 to 380 duplicates per 10,000 (the earlier README run used 2,000). The benchmark now records
`webhookLagMs`, and a sweep of the reconcile window at seed 1, 10% faults, 10,000 messages,
showed why (`bench/results/mailchaos-before-pump-fix/`):

| Policy | Window | Duplicates | Unresolved | Webhook lag p50 / p99 |
|---|---|---|---|---|
| RetryAfterWindow | 2 s | 358 | 0 | 6.2 s / 7.3 s |
| RetryAfterWindow | 10 s | 0 | 0 | 5.6 s / 6.8 s |
| RetryAfterWindow | 30 s | 0 | 1 | 5.4 s / 6.9 s |
| Hold | — | 0 | 428 | 5.3 s / 6.6 s |

The evidence was arriving after the window, so the resend was a wrong guess. The cause was in
the benchmark, not in Relay: its single webhook consumer slept 5 ms whenever the event at the
head of the queue wasn't due yet, even with due events behind it, and every "retry later"
event went back in 200 ms ahead. Under load those stalls added up to seconds; one run reached
a 33 s p99. A real webhook endpoint serves requests concurrently and never waits like that.
Now the consumer sleeps only after a full pass finds nothing due, and four consumers run
concurrently. Lag dropped to milliseconds and the duplicates went with it.

The underlying lesson holds for production: **`RetryAfterWindow` duplicates exactly when
evidence arrives later than the window.** That is why production defaults to `Hold`.

### What `Hold` costs

`Hold` never guesses, so it never duplicates, but a message whose provider never got it stays
Unknown until someone acts. In the sweep above that was 428 of 10,000 messages at a 10% fault
rate. The runbook covers what to do with them. A one-command "resolve Unknown" tool would make
that easier; it doesn't exist yet.

Delivery latency, measured from first submission to first provider acceptance, is higher in
these runs than in the old 2,000-message headline: p50 about 4 s against about 20 ms, for both
outbox designs. "Outbox only" has no webhook consumer, so the consumer fix isn't the cause.
The likely cause is the idempotency-lookup fix in section 5: accepts got fast enough that
10,000 submissions now outpace the four workers, and messages wait in the queue. This has not
been verified separately. Total run time went down (about 24 s to 19–22 s at 10% faults).

## 4. Real crash test

MailChaos simulates crashes in-process. `bench/crash-test.sh` does the real thing:

- the full compose stack plus `bench/stack/compose.bench.yaml`, which sets a 10 s lease;
- real PostgreSQL, and real SMTP delivery into Mailpit;
- three worker containers;
- `docker kill -s KILL` on a random worker every 1–1.5 s while they deliver.

At the end it compares what reached the inbox, matched by `Message-ID`, with what the database
believes.

| Run | Messages | Kills during delivery | Lost | Duplicates | Unknown at end | …of which had arrived |
|---|---|---|---|---|---|---|
| crash-1795632866 | 3,000 | 20 (total) | 0 | 0 | 4 | 2 |
| crash-1795632934 | 5,000 | 28 | 0 | 0 | 10 | 7 |
| crash-1795632994 | 5,000 | 27 | 0 | 0 | 9 | 7 |
| crash-1795633056 | 5,000 | 26 | 0 | 0 | 9 | 3 |
| crash-1795633117 | 5,000 | 28 | 0 | 0 | 8 | 6 |

The first run predates the "kills during delivery" counter, so it shows the total.

**How to read the Unknown column.** A worker killed mid-send leaves a message whose fate it
can't know. Relay marks it Unknown instead of resending it. In 25 of those 40 cases the mail
had in fact arrived, so a blind resend would have been a duplicate. The other 15 (0.07% of the
23,000) had not arrived. They are not lost: they are flagged, listed and explained by
`/v1/messages/{id}/explain`. But they wait for an operator, because Mailpit, like any plain
SMTP server, sends no delivery webhooks to settle them. With an API provider such as Resend,
an idempotent retry settles them automatically. "Lost" in the table counts only messages the
database thinks are fine but that never arrived; that was 0.

## 5. Load test

`bench/LoadTest` is an open-loop generator. Request *i* is scheduled at `t0 + i/rate` and fired
on time, however slow earlier responses are. Latency is measured from the scheduled time, so a
struggling server can't hide its queueing (no coordinated omission). Setup:

- one tenant, `POST /v1/messages` with a unique `Idempotency-Key`;
- 30 s per step after a 5 s warm-up;
- API and PostgreSQL limited to 2 vCPU each;
- the rate limiter lifted so the test measures the service itself.

Status `-1` means the client gave up after 30 s.

**Before** (`bench/results/load/accept.json`):

| Rate/s | 2xx | Errors | p50 | p99 |
|---|---|---|---|---|
| 100 | 3,000 | 0 | 4.6 ms | 8.7 ms |
| 250 | 7,500 | 0 | 4.5 ms | 11.0 ms |
| 500 | 2,031 | 12,969 (1,458 × 500, rest timeouts) | 15.3 s | 29.8 s |

Three causes, found in the API logs and `pg_stat_activity`:

1. **Too many connections.** API and worker each opened up to Npgsql's default 100, against
   PostgreSQL's default `max_connections` of 100, so PostgreSQL refused connections ("53300:
   sorry, too many clients"). Fix: pool size 50 per process, `max_connections=200`.
2. **A full table scan on every send.** The idempotency lookup matched
   `key = k OR key LIKE 'k#%'` (for bulk children), which no B-tree index can serve, so every
   request scanned the whole `messages` table (44,540 rows at the time, per `EXPLAIN ANALYZE`).
   Fix: two point lookups on the unique index first (`k` and `k#0`, which exists whenever any bulk
   child does). The prefix query runs only on a real replay.
3. **Hot tenant row.** The quota `UPDATE tenants ...` ran first in the accept transaction, so
   the row lock was held through the inserts as well, and requests queued on it
   (`Lock:transactionid` was the top wait). Fix: charge the quota last, just before `COMMIT`.
   It is still one atomic statement, and a refusal still rolls the inserts back. The quota-race
   and idempotency integration tests still pass.

**After** (`bench/results/load/accept-after.json`):

| Rate/s | 2xx | Errors | p50 | p95 | p99 |
|---|---|---|---|---|---|
| 100 | 3,000 | 0 | 3.0 ms | 4.3 ms | 7.7 ms |
| 250 | 7,500 | 0 | 2.3 ms | 4.3 ms | 26.8 ms |
| 500 | 15,000 | 0 | 2.1 ms | 3.5 ms | 9.5 ms |
| 750 | 19,838 | 2,662 timeouts | 3.6 ms | 25.4 s | 29.2 s |

These before/after runs show the three fixes working: 500 req/s went from mostly failing to
clean. Treat the absolute numbers as indicative only. Each is a single run on a laptop that was
also running other apps, and section 5.1 shows how far such runs drift.

### 5.1 One tenant against twenty: inconclusive on a laptop

Each request serialises on its tenant's row (the atomic quota update), so spreading load over
many tenants should raise the ceiling. `bench/run-load.sh` tests this from a clean slate each
time: a fresh database, the worker stopped (the accept path only), API and PostgreSQL limited to
2 vCPU each, and N fresh tenants. The load generator sends requests round-robin over their keys
(`--keys-file`). It ran two rounds of 1 tenant against 20, alternated
(`bench/results/load/laptop-variance/`):

| Run | 500/s | 750/s | 1,000/s | 1,250/s | 1,500/s |
|---|---|---|---|---|---|
| 1 tenant, round 1 | 0 errors, p99 87 ms | 0 errors, p99 9 ms | 2,832 timeouts | 30,783 timeouts | 41,851 timeouts |
| 20 tenants, round 1 | 0 errors, p50 3.7 s | 0 errors, p50 2.8 s | 0 errors, p50 12.3 s | 6,187 timeouts | 18,957 timeouts |
| 1 tenant, round 2 | 8,112 timeouts | 18,106 timeouts | 26,400 timeouts | 33,588 timeouts | 40,244 timeouts |
| 20 tenants, round 2 | 0 errors, p50 9.1 s | 0 errors, p50 5.4 s | 2,516 timeouts | 8,945 timeouts | 0 errors, p50 22.6 s |

The same configuration gave opposite results between rounds. One tenant was clean at 750/s in
round 1 and failed at 500/s in round 2. Load average on the host stayed at 9–11 on 10 cores even
with the benchmark idle, because other desktop apps were using the CPU. No ceiling can be read
from this, and none is claimed. The manually triggered `load` workflow
(`.github/workflows/load.yml`) runs the same script on a clean GitHub runner, three rounds of each
configuration, and uploads the results. Its numbers replace this section once it has run.

## 6. Mutation testing

Coverage says a line ran; mutation testing says whether a test would notice if it were wrong.
Stryker.NET (`bench/run-mutation.sh`) makes small changes to the code (flips a comparison, drops
a statement, swaps `&&` for `||`) and reruns the unit tests against each one. A mutant that no
test notices *survives*. It covers the pure layers; Infrastructure is covered by integration tests,
which are too slow to rerun for every mutant.

Getting it to run at all needed one fix: the secret-scanner theory built its random fake keys
into the test names, so the names differed between discovery and execution and Stryker refused
the initial run. The fake keys are now built inside the test, and the names are stable.

The first run was low (34.0% Domain, 37.9% Guard, 53.1% Application). The survivors showed real
gaps: almost every limit was tested on one side only. These were not tested:

- the Guard's caps at exactly the cap;
- cc and bcc address validation;
- the card scanner's 13- and 19-digit edges;
- idempotency-key character and length limits;
- the bulk-key suffix;
- tenant pause and resume.

`tests/Relay.UnitTests/BoundaryTests.cs` adds 38 tests aimed at those survivors, most of which
pin a limit from both sides. After (`bench/results/mutation/summary.json`):

| Project | Before | After | After, covered code only |
|---|---|---|---|
| Relay.Domain | 34.0% | 55.3% | 63.4% |
| Relay.Guard | 37.9% | 49.7% | 88.3% |
| Relay.Application | 53.1% | 66.0% | 73.6% |

"Covered code only" leaves out mutants in code no unit test reaches. For the Guard that is mainly
`AgentGuardService` (127 mutants), which the integration tests exercise through the API.

Most of the remaining survivors are equivalent mutants: they change nothing observable. Examples:

- Default `string.Empty` values on EF-mapped properties.
- Argument guards whose exception no caller can trigger.
- `d >= 9` in the Luhn check, where a doubled digit can never be 9.
- Telemetry counters.

The ones worth a test next are in `MessageExplainer` (wording) and `SendMessageHandler`'s replay
path.

## 7. AgentMailBench with a live model

AgentMailBench's 150 scenarios are scripted: the attacks are fixed tool calls. `bench/LiveAgentBench`
puts a real Claude model in the agent seat instead. The model gets `read_document`, `send_email`
and `request_approval`, a neutral system prompt, and a task from the user. Every send goes
through the running stack's agent endpoint. The scenarios are:

- 12 benign tasks;
- 15 prompt injections hidden in a document (with a fake secret to steal);
- 9 redirects, where an email the agent reads asks it to send somewhere new.

Each scenario runs twice, Guard off and Guard on. It measures:

- how often the model itself attempts a harmful send;
- how many harmful sends actually go out;
- whether the user's task still gets done;
- false blocks.

Status: the harness builds and is ready, but it has not been run yet (it needs a Claude API key).

## 8. Not done

- **Network faults through Toxiproxy.**
- **A trustworthy load ceiling**, from the `load` workflow on a clean runner (section 5.1).

## Reproduce

```bash
dotnet test                                             # all suites
bench/coverage.sh                                       # coverage summary
bench/run-mailchaos.sh                                  # README headline (10,000 per run)
dotnet run -c Release --project bench/MailChaos -- --rates 0.05,0.1 --seed 3 --out /tmp/s3.json
docker compose -f compose.yaml -f bench/stack/compose.bench.yaml up -d --build
bench/crash-test.sh <api-key> 5000 3 1.0 45
bench/run-load.sh 20 my-run                            # fresh stack, 20 tenants
bench/run-mutation.sh                                   # Stryker on Domain, Guard, Application
dotnet run -c Release --project bench/LoadTest -- --key <api-key>    # targets the bench overlay's API port
```
