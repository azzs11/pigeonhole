# Contributing

Thanks for wanting to help sort the mail.

## Set up

- .NET 10 SDK (pinned in `global.json`), Docker, Node 24.
- `docker compose -f deploy/docker-compose.yml up -d` for PostgreSQL (:5433) and Mailpit.
- `dotnet build && dotnet test`. Integration tests start their own containers with Testcontainers.
- `cd web && npm ci && npm test`. For `npm run e2e` you also need the API and the worker running (see the README).

## House rules

- **Small pull requests, Conventional Commits** (`feat(api): …`, `fix(worker): …`, `docs: …`).
  release-please builds the changelog from them.
- **Tests come with the change.** `dotnet format --verify-no-changes` must pass, and warnings are errors.
- **Dependencies point inward** (Domain ← Application ← Infrastructure/Api). The architecture tests enforce it.
- **No mutable static state.** Use `TimeProvider` for time and `Guid.CreateVersion7()` for IDs, and pass a `CancellationToken` to all I/O.
- **Never commit a secret**, even a fake one shaped like a real key. Generate test secrets at
  runtime. gitleaks runs in CI.
- **No invented numbers.** Figures in docs come from `bench/results/` or from output you show.
- Significant decisions get a short ADR in `docs/adr/`.
- After API changes, regenerate the dashboard client: `web/scripts/gen-api.sh`.
- New packages: say why in the pull request. MIT or Apache licences are preferred. No MassTransit.
