#!/usr/bin/env bash
# Mutation testing with Stryker.NET: change the code in small ways (flip a comparison, drop a
# statement, swap a boolean) and check that some unit test fails each time. A surviving mutant is
# a change no test notices. Covers the pure layers; Infrastructure is covered by integration tests,
# which are too slow to rerun per mutant.
# Usage: bench/run-mutation.sh [Domain Guard Application]
# Writes bench/results/mutation/summary.json and an HTML report per project (not committed).
set -euo pipefail
cd "$(dirname "$0")/.."
root=$(pwd)
projects=("${@:-Domain Guard Application}")
read -r -a projects <<< "${projects[*]}"
dotnet tool restore >/dev/null
for p in "${projects[@]}"; do
  (cd tests/Relay.UnitTests && dotnet stryker --config-file "$root/bench/mutation/stryker-$p.json" \
    --output "$root/bench/results/mutation/$p" | grep -E "mutation score|Killed|Survived|Timeout|No coverage")
done
python3 -I bench/mutation/summarize.py "$root/bench/results/mutation" "${projects[@]}"
