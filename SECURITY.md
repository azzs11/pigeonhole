# Security policy

## Reporting a vulnerability

Please **don't open a public issue**. Use GitHub's private vulnerability reporting
(Security → Report a vulnerability) on this repository. Expect a reply within a week.

In scope: authentication and tenant isolation, webhook signature checks, the Agent Send Guard
(a way past it that [docs/threat-model.md](docs/threat-model.md) doesn't already list), the
sandbox sending real mail, and secrets leaking into logs or responses.

Already documented and out of scope: the Guard limitations listed in the threat model.

## What the project does by design

- API keys (`rly_…`) are stored as hashes and compared in constant time. They are shown once.
- Every query is scoped to the caller's tenant.
- Unexpected errors return ProblemDetails with a trace ID, never the exception text.
- Addresses are masked and bodies are never written to logs at Information level.
- The sandbox refuses API providers, and the public demo only talks to Mailpit.
- CI runs gitleaks on every push. Release images are signed with cosign (keyless).
