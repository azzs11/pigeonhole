#!/usr/bin/env bash
# One command: run AgentMailBench, regenerate the README table, and check the full Guard's thresholds.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=bench/results/agentmailbench/results.json
dotnet run -c Release --project bench/AgentMailBench -- "$@" --out "$OUT"
python3 bench/agentmailbench.py "$OUT"
