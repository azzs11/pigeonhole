"""Summarise Stryker JSON reports into bench/results/mutation/summary.json.

score        = detected / (detected + survived + no coverage)   (Stryker's own definition)
scoreCovered = detected / (detected + survived)                  (only code the unit tests reach)
Usage: python3 bench/mutation/summarize.py <results-dir> Domain Guard Application
"""

import collections
import datetime
import json
import pathlib
import sys

out, names = pathlib.Path(sys.argv[1]), sys.argv[2:]
summary = {
    "benchmark": "mutation testing (Stryker.NET, unit tests)",
    "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "projects": {},
}
for name in names:
    report = json.loads((out / name / "reports" / "mutation-report.json").read_text())
    status = collections.Counter(m["status"] for f in report["files"].values() for m in f["mutants"])
    detected = status["Killed"] + status["Timeout"]
    score = round(100 * detected / max(1, detected + status["Survived"] + status["NoCoverage"]), 1)
    covered = round(100 * detected / max(1, detected + status["Survived"]), 1)
    summary["projects"][f"Relay.{name}"] = {**dict(status), "score": score, "scoreCovered": covered}
    print(f"Relay.{name:12} score {score:5.1f}%  covered-only {covered:5.1f}%  {dict(status)}")
(out / "summary.json").write_text(json.dumps(summary, indent=2))
