# 0010. One-command stack, public sandbox and releases

- Status: accepted
- Date: 2026-11-26

## Decision

- **`docker compose up`** at the repo root (`compose.yaml`) includes `deploy/docker-compose.yml`
  (PostgreSQL, Mailpit) and adds the API, one worker and the dashboard (nginx serving the
  Vite build and proxying `/v1`, `/docs` and `/openapi`). The API port is not published.
- **Sandbox, no signup:** `POST /v1/sandbox/demo-key` (sandbox only) creates a throwaway tenant
  with all scopes and `DemoDailyQuota` (200). It is limited to 5 per client IP per hour. Behind
  nginx the client IP comes from `X-Forwarded-For`, trusted only from `Relay:Proxy:KnownNetworks`.
  Without that, every visitor would share nginx's single bucket.
- **Can't send real mail:** `SandboxOptions.EnsureSinkOnly` makes the worker refuse to start with
  an API provider in sandbox mode. The stack's one SMTP provider is Mailpit.
- **CI `compose` job:** builds the images, runs `docker compose up`, then `deploy/smoke.sh` (demo
  key → send → exactly one letter in Mailpit).
- **Releases:** release-please (`release-please-config.json`, `release-as: 1.0.0`) keeps a release
  pull request open from Conventional Commits. Merging it tags `v1.0.0` and writes the notes.
  `release.yml` then builds the API, worker and dashboard images, pushes them to GHCR with
  provenance and an SBOM, and signs each digest with cosign (keyless, GitHub OIDC).
- **CI flake removed:** the AgentMailBench p99 latency target is gated by the benchmark script
  only, not by the parallel integration suite (it read 10.41 ms on a shared runner).

## Consequences

- The public sandbox needs a host. This repo ships the config and the checklist
  (docs/runbook.md); deploying it is an operator step.
- Signing depends on `id-token: write`. Verify with
  `cosign verify ghcr.io/azzs11/pigeonhole-api:1.0.0 --certificate-identity-regexp 'github.com/azzs11/pigeonhole' --certificate-oidc-issuer https://token.actions.githubusercontent.com`.
