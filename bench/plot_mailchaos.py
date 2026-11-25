"""Plot MailChaos results: duplicates and losses per 10,000 against fault rate, one line per system."""
import json
import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

results_path = Path(sys.argv[1] if len(sys.argv) > 1 else "bench/results/mailchaos/results.json")
out_path = Path(sys.argv[2] if len(sys.argv) > 2 else results_path.with_name("mailchaos.png"))
data = json.loads(results_path.read_text())

styles = {
    "naive send-then-save": ("#c2410c", "o"),
    "outbox only": ("#a16207", "s"),
    "outbox + unknown state": ("#15803d", "D"),
}
fig, axes = plt.subplots(1, 2, figsize=(11, 4.2), sharex=True)
for system in dict.fromkeys(r["system"] for r in data["runs"]):
    runs = sorted((r for r in data["runs"] if r["system"] == system), key=lambda r: r["faultRate"])
    x = [r["faultRate"] * 100 for r in runs]
    color, marker = styles.get(system, (None, "o"))
    axes[0].plot(x, [r["duplicatesPer10k"] for r in runs], marker=marker, color=color, label=system, linewidth=2)
    axes[1].plot(x, [r["lostPer10k"] for r in runs], marker=marker, color=color, label=system, linewidth=2)

for ax, title in zip(axes, ["Duplicate sends per 10,000", "Lost messages per 10,000"]):
    ax.set_title(title)
    ax.set_xlabel("Injected fault rate per provider call (%)")
    ax.grid(alpha=0.3)
    ax.set_ylim(bottom=0)
axes[0].legend(frameon=False)
fig.suptitle(
    f"MailChaos: {data['messagesPerRun']:,} messages per run, seed {data['seed']}, commit {data['commit']}",
    fontsize=10, color="#555")
fig.tight_layout()
fig.savefig(out_path, dpi=150)
print(f"Wrote {out_path}")
