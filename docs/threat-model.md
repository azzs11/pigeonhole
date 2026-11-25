# Agent Send Guard: threat model

The Guard is **defence in depth**, not a proof of safety. It narrows what an AI agent can do
with an email tool. It cannot make a compromised agent trustworthy.

## Setting

A human or an app starts an **agent run** with `POST /v1/agent-runs` (scope `agents:manage`).
The run names the trusted recipients, the allowed domains and the caps. The agent gets a
**run token**, which can only call `/v1/agent/*` within that run. Every check runs inside the
API. The MCP server is a thin client, so a misconfigured or malicious MCP client can't skip
the checks.

## What it defends against

| Threat | Defence |
|---|---|
| Prompt injection telling the agent to mail data to an attacker | **Recipient provenance.** An address not named on the run and not at an allowed domain is held for a human, or blocked if the content is also risky. |
| Attacker added to CC/BCC of a legitimate email | Provenance applies to every recipient field. |
| Lookalike domains (`examp1e.test` for `example.test`) | Domains match exactly; a lookalike is untrusted. |
| Credentials or personal data in outgoing mail | **Secret and PII scan**: private keys, cloud and SaaS tokens, JWTs, `password: …` assignments, SSNs, Luhn-valid card numbers, and high-entropy tokens. Findings raise the risk score. |
| An agent retrying a tool call and mailing someone five times | **Near-duplicate suppression.** The same normalized content to the same recipient within 24 h returns the original message. |
| Runaway loops and blasts | **Caps**: recipients per message (10), distinct recipients per run (25), sends per hour (30), sends per recipient per day (3). Caps are hard blocks. |
| An agent approving its own mail | Approval needs `agents:manage`, which a run token never has. Every decision is in the audit trail. |
| Approvals left open forever | Pending approvals expire (default 60 min) and their messages are rejected. |
| A leaked run token | It is scoped to one run, expires with it, and is stored hashed. |

## What it does not defend against

Each of these is a real gap. AgentMailBench keeps scenarios for some of them in the
corpus, so the block rate is not inflated.

- **Compromised trusted parties.** Mail to an address the run trusts, or to any address at an
  allowed domain, is not questioned on provenance. An insider or a compromised mailbox at an
  allowed domain receives whatever the agent sends, as long as it contains no detectable secret.
  *(AgentMailBench: exfil-39.)*
- **Secrets the scanner can't see.** Encodings (base64, spaced-out characters, split across
  messages), paraphrased secrets, images and attachments all get through. The scanner
  recognizes shapes, not meaning. *(AgentMailBench: exfil-40.)*
- **Sensitive data that isn't shaped like a secret.** Business information (revenue, customer
  names, plans) sent to a trusted recipient is allowed. The Guard does no data classification.
- **Reworded retries.** Duplicate detection compares normalized content, so a paraphrase
  is a new message. The per-recipient daily cap still limits the damage.
  *(AgentMailBench: retry-24, retry-25.)*
- **Adaptive attackers.** Someone who knows the rules can stay under every cap and threshold.
  The caps bound the damage; they don't prevent it.
- **Compromised run setup.** If whoever creates the run is fooled into trusting an attacker's
  address, the Guard enforces the wrong envelope faithfully.
- **Human approval fatigue.** A held message is only as safe as the person reviewing it.
  The approval card shows the reasons and the risk score; it can't make the person read them.
- **Everything after delivery.** Phishing in the content, links and reputation damage are the
  recipient's and the provider's problem, apart from the bounce and complaint suppression added in Phase 3.

## Measured

See [benchmarks/agentmailbench.md](benchmarks/agentmailbench.md). The full Guard stops 95.6% of
the scripted attack scenarios, with 0% false blocks and 0% approvals on the benign scenarios.
The four attacks it misses are the limitations above.
