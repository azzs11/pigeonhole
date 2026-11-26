"""Merge Cobertura reports from several test projects into one line-coverage summary per project.

Each report stores file names relative to its own <source> roots, so files are resolved to absolute
paths before merging; a line counts as covered if any test project hit it. EF migrations and
generated code (obj/) are excluded.

Usage: python3 bench/coverage.py <out.json> <coverage.cobertura.xml>...
"""

import collections
import datetime
import json
import os
import sys
import xml.etree.ElementTree as ET

out, reports = sys.argv[1], sys.argv[2:]
hits: dict[tuple[str, str, str], int] = collections.defaultdict(int)
for report in reports:
    root = ET.parse(report).getroot()
    sources = [s.text or "" for s in root.iter("source")]
    for package in root.iter("package"):
        name = package.get("name", "")
        if not name.startswith("Relay."):
            continue
        for cls in package.iter("class"):
            rel = cls.get("filename", "")
            full = next((os.path.normpath(os.path.join(s, rel)) for s in sources if os.path.exists(os.path.join(s, rel))), rel)
            if "Migrations" in full or "/obj/" in full:
                continue
            for line in cls.iter("line"):
                key = (name, full, line.get("number", ""))
                hits[key] = max(hits[key], int(line.get("hits", "0")))

total: collections.Counter[str] = collections.Counter()
covered: collections.Counter[str] = collections.Counter()
for (project, _, _), h in hits.items():
    total[project] += 1
    covered[project] += h > 0

projects = {p: {"covered": covered[p], "lines": total[p], "percent": round(100 * covered[p] / total[p], 1)} for p in sorted(total)}
summary = {
    "benchmark": "line coverage (unit + integration tests, merged)",
    "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "excluded": ["EF migrations", "generated code under obj/"],
    "projects": projects,
    "all": {
        "covered": sum(covered.values()),
        "lines": sum(total.values()),
        "percent": round(100 * sum(covered.values()) / max(1, sum(total.values())), 1),
    },
}
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out, "w", encoding="utf-8") as f:
    json.dump(summary, f, indent=2)
for p, v in projects.items():
    print(f"{p:22} {v['covered']:5}/{v['lines']:<5} {v['percent']:5.1f}%")
print(f"{'all':22} {summary['all']['covered']:5}/{summary['all']['lines']:<5} {summary['all']['percent']:5.1f}%")
