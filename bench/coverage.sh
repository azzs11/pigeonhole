#!/usr/bin/env bash
# Line coverage of src/ across the unit and integration suites (needs Docker for Testcontainers).
# Writes bench/results/coverage/summary.json.
set -euo pipefail
cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
for project in tests/Relay.UnitTests tests/Relay.IntegrationTests; do
  dotnet test "$project" --collect:"XPlat Code Coverage" --results-directory "$work/$(basename "$project")"
done
python3 bench/coverage.py bench/results/coverage/summary.json $(find "$work" -name coverage.cobertura.xml)
