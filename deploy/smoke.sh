#!/usr/bin/env bash
# Smoke test for a running sandbox stack: get a demo key, send one message, see it arrive in Mailpit.
# Usage: deploy/smoke.sh [dashboard-url] [mailpit-url]   (defaults: the `docker compose up` ports)
set -euo pipefail
base="${1:-http://localhost:5173}"
mailpit="${2:-http://localhost:8025}"

for _ in $(seq 1 90); do curl -sf "$base/v1/sandbox" >/dev/null && break; sleep 2; done
curl -sf "$base/v1/sandbox" | grep -q '"enabled":true' || { echo "sandbox is not on at $base"; exit 1; }

key=$(curl -sf -X POST "$base/v1/sandbox/demo-key" | python3 -c 'import sys, json; print(json.load(sys.stdin)["key"])')
subject="smoke-$(date +%s)-$RANDOM"
curl -sf -o /dev/null -H "Authorization: Bearer $key" -H "Idempotency-Key: $subject" -H "Content-Type: application/json" \
  -d "{\"to\":[\"smoke@example.test\"],\"subject\":\"$subject\",\"text\":\"hello from the smoke test\"}" "$base/v1/messages"
echo "accepted: $subject"

for _ in $(seq 1 60); do
  if curl -sf "$mailpit/api/v1/search?query=$(python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1]))' "subject:\"$subject\"")" \
    | grep -q '"messages_count":1'; then
    echo "delivered to the test inbox exactly once"
    exit 0
  fi
  sleep 1
done
echo "not in the test inbox after 60 s"; exit 1
