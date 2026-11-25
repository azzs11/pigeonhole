"""Render the MailChaos headline table into README.md between the mailchaos markers, from results.json only."""
import json
import re
import sys
from pathlib import Path

results = json.loads(Path(sys.argv[1] if len(sys.argv) > 1 else "bench/results/mailchaos/results.json").read_text())
readme = Path("README.md")
rates = sorted({r["faultRate"] for r in results["runs"]})
systems = list(dict.fromkeys(r["system"] for r in results["runs"]))
by = {(r["system"], r["faultRate"]): r for r in results["runs"]}

def cell(r):
    return f"{r['duplicatesPer10k']:.0f} / {r['lostPer10k']:.0f}"

lines = [
    f"Duplicates / lost per 10,000 messages ({results['messagesPerRun']:,} per run, seed {results['seed']}, "
    f"commit `{results['commit']}`, generated {results['generatedAt'][:10]}). Fault rate is per provider call.",
    "",
    "| System | " + " | ".join(f"{r * 100:g}% faults" for r in rates) + " |",
    "|---|" + "---|" * len(rates),
]
lines += [f"| {s} | " + " | ".join(cell(by[(s, r)]) for r in rates) + " |" for s in systems]
lines += ["", "![MailChaos results](bench/results/mailchaos/mailchaos.png)"]
block = "<!-- mailchaos:start -->\n" + "\n".join(lines) + "\n<!-- mailchaos:end -->"

text = readme.read_text()
new = re.sub(r"<!-- mailchaos:start -->.*?<!-- mailchaos:end -->", block, text, flags=re.S)
if new == text and block not in text:
    sys.exit("README.md has no <!-- mailchaos:start --> ... <!-- mailchaos:end --> markers")
readme.write_text(new)
print("Updated README.md headline results")
