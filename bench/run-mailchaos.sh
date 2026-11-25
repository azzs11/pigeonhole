#!/usr/bin/env bash
# One command: run MailChaos, plot it, and regenerate the README headline from the results file.
# Usage: bench/run-mailchaos.sh [--quick] [--messages N] [--rates 0,0.05] [--seed 42]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=bench/results/mailchaos/results.json

dotnet run -c Release --project bench/MailChaos -- "$@" --out "$OUT"

if [ ! -x bench/.venv/bin/python ]; then
  python3 -m venv bench/.venv
  bench/.venv/bin/pip install --quiet matplotlib
fi
bench/.venv/bin/python bench/plot_mailchaos.py "$OUT"
python3 bench/update_readme.py "$OUT"
python3 bench/check_mailchaos.py "$OUT"
