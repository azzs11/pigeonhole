"""Gate for the nightly run: the full system must not lose mail and must beat both baselines on duplicates."""
import json
import sys

data = json.load(open(sys.argv[1]))
failures = []
for rate in sorted({r["faultRate"] for r in data["runs"]}):
    runs = {r["system"]: r for r in data["runs"] if r["faultRate"] == rate}
    full = runs["outbox + unknown state"]
    if full["lost"] > 0:
        failures.append(f"{rate:.0%}: outbox + unknown state lost {full['lost']}")
    for baseline in ("naive send-then-save", "outbox only"):
        if rate > 0 and full["duplicates"] > runs[baseline]["duplicates"]:
            failures.append(f"{rate:.0%}: more duplicates than {baseline}")
    if full["duplicatesPer10k"] >= 1 and rate <= 0.05:
        failures.append(f"{rate:.0%}: {full['duplicatesPer10k']:.1f} duplicates per 10k (target < 1)")
print("\n".join(failures) or "MailChaos gate passed")
sys.exit(1 if failures else 0)
