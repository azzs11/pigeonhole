import type { Schemas } from "@/api/client";
import { useMessage } from "@/api/hooks";
import { Button } from "./ui/button";
import { cn, num } from "@/lib/utils";

type Props = { approval: Schemas["ApprovalRequest"]; onDecide: (approve: boolean) => void; busy?: boolean };

/** One held message. Explain every block: the reasons are the content. Keyboard: A approves, R rejects. */
export function ApprovalCard({ approval, onDecide, busy }: Props) {
  const message = useMessage(approval.messageId ?? "");
  const score = num(approval.riskScore);
  const m = message.data?.message;
  return (
    <article
      tabIndex={0}
      aria-label={`Held message to ${m?.to.join(", ") ?? "…"}, risk ${score}. Press A to approve or R to reject.`}
      onKeyDown={(e) => {
        if (busy || e.target !== e.currentTarget) return;
        if (e.key === "a" || e.key === "A") onDecide(true);
        if (e.key === "r" || e.key === "R") onDecide(false);
      }}
      className="rounded-lg border border-line bg-panel p-4"
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="font-sign text-xs tracking-widest text-muted uppercase">
            Held by {approval.source === "agent" ? "the agent's request" : "the guard"}
          </div>
          <h3 className="text-lg font-semibold">{m?.subject ?? "…"}</h3>
          <div className="text-sm">
            To <span className="font-log">{m ? [...m.to, ...m.cc, ...m.bcc].join(", ") : "…"}</span>
          </div>
        </div>
        <div className="text-right">
          <div className="font-sign text-xs tracking-widest text-muted uppercase">Risk</div>
          <div className={cn("font-log text-2xl font-semibold", score >= 60 ? "text-bad" : "text-warn")}>{score}/100</div>
        </div>
      </div>
      <ul className="mt-3 list-disc space-y-1 pl-5 text-sm">
        {(approval.reasons ?? []).map((r) => (
          <li key={r}>{r}</li>
        ))}
      </ul>
      <div className="mt-4 flex gap-2">
        <Button variant="good" onClick={() => onDecide(true)} disabled={busy}>
          Approve <kbd className="font-log text-xs opacity-80">A</kbd>
        </Button>
        <Button variant="bad" onClick={() => onDecide(false)} disabled={busy}>
          Reject <kbd className="font-log text-xs opacity-80">R</kbd>
        </Button>
      </div>
    </article>
  );
}
