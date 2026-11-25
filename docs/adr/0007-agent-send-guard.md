# 0007. Agent Send Guard and MCP server

- Status: accepted
- Date: 2026-11-25

## Decision

- **Enforcement lives in the API.** An agent run (`POST /v1/agent-runs`, scope `agents:manage`)
  defines the send envelope: trusted recipients, allowed domains, caps and thresholds. It is
  stored as `jsonb`. The run token is an API key with only `agent:send` plus a `run_id` claim,
  so it can't reach `/v1/messages` or approvals.
- **The decision is a pure function** (`GuardEvaluator.Evaluate(input, facts, policy)`). The facts
  (run recipients, sends in the last hour and per recipient today, near-duplicates) come from
  four indexed SQL queries. Caps block outright. Otherwise the risk score is provenance
  (50 for the first untrusted recipient, +10 for each additional one, at most 80) plus the most
  severe finding (high 60, medium 45, low 25). Below 40 is allowed, 40–79 is held, 80 and above
  is blocked.
- **Held** messages are stored in `Held` with no outbox row. Approval moves them to Accepted and
  adds the outbox row in one transaction; rejection or expiry moves them to Rejected. Every
  decision is written to `guard_audit`.
- **Dry run** (`?dryRun=true`) evaluates and returns a preview with no writes, including no audit entry.
- **MCP server** (`Relay.Mcp`, official C# SDK, stdio) has four tools: `send_email`, `list_sent`,
  `get_status` and `request_approval`. It forwards to `/v1/agent/*` with the run token and returns
  the Guard's reasons to the agent verbatim, so the agent can explain or ask for approval.
  `relay runs create` prints a ready `claude mcp add …` line.

## Consequences

- An agent can't loosen its own envelope or approve its own mail.
- The plan's `docker compose --profile mcp` isn't used: a stdio server is started by the MCP
  client, not run as a long-lived container.
