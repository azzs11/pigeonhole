#!/usr/bin/env bash
# Regenerate the typed API client: export the OpenAPI document from a running API, then generate TypeScript types.
# Usage: web/scripts/gen-api.sh [api-base-url]   (default http://localhost:5238)
set -euo pipefail
cd "$(dirname "$0")/.."
curl -sf "${1:-http://localhost:5238}/openapi/v1.json" | python3 -m json.tool --indent 2 > openapi/relay.json
npx openapi-typescript openapi/relay.json -o src/api/schema.d.ts
