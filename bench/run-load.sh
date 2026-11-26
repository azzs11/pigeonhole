#!/usr/bin/env bash
# Accept-path load test from a clean slate, so runs are comparable:
#   fresh database, worker stopped (the accept path alone, as if workers ran elsewhere),
#   API and PostgreSQL pinned to 2 vCPU each, N fresh tenants with a high quota.
# Usage: bench/run-load.sh <tenants> <label> [rates=500,750,1000,1250,1500]
# Writes bench/results/load/<label>.json. Keys live in a temp dir that is deleted afterwards.
set -euo pipefail
cd "$(dirname "$0")/.."
tenants="$1"; label="$2"; rates="${3:-500,750,1000,1250,1500}"
compose=(docker compose -f compose.yaml -f bench/stack/compose.bench.yaml)
keys=$(mktemp -d); trap 'rm -rf "$keys"' EXIT

"${compose[@]}" down -v --remove-orphans >/dev/null 2>&1
"${compose[@]}" up -d postgres mailpit api >/dev/null 2>&1
docker update --cpus 2 pigeonhole-postgres-1 >/dev/null
for _ in $(seq 1 60); do curl -sf -o /dev/null http://localhost:8080/health && break; sleep 1; done

export ConnectionStrings__Relay="Host=localhost;Port=5433;Database=relay;Username=relay;Password=relay_local_dev"
dotnet build tools/Relay.Cli -v q >/dev/null
for i in $(seq 1 "$tenants"); do
  dotnet run --no-build --project tools/Relay.Cli -- keys create --tenant "load-$i" | awk '/^API key:/{print $3}' >> "$keys/keys.txt"
  dotnet run --no-build --project tools/Relay.Cli -- tenants quota --tenant "load-$i" --daily 100000000 >/dev/null
done

dotnet run -c Release --project bench/LoadTest -- --url http://localhost:8080 --keys-file "$keys/keys.txt" --rates "$rates" \
  --label "api 2 vCPU, postgres 2 vCPU, $tenants tenant(s), worker stopped, fresh database" --out "bench/results/load/$label.json"
