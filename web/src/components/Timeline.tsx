import type { CSSProperties } from "react";
import type { Schemas } from "@/api/client";
import { StampBadge } from "./StampBadge";

/** State first, logs second: each step shows its stamp, its time and the plain-language reason. */
export function Timeline({ explanation }: { explanation: Schemas["Explanation"] }) {
  return (
    <div>
      <p className="mb-4 border-l-8 border-ink bg-wrap-blue p-3 text-sm text-on-wrap" aria-live="polite">
        {explanation.summary}
      </p>
      <ol className="relative space-y-4 border-l-2 border-ink pl-5">
        {explanation.steps.map((step, i) => (
          <li key={i} className="relative" style={{ "--d": `${i * 160}ms` } as CSSProperties}>
            <span className="absolute top-1.5 -left-[27px] size-3 rounded-full border-2 border-ink bg-panel" aria-hidden />
            <div className="flex flex-wrap items-center gap-2">
              <StampBadge status={step.to} thunk className="[animation-delay:var(--d)]" />
              <time className="font-log text-xs text-muted" dateTime={step.at}>
                {new Date(step.at).toLocaleTimeString()}
              </time>
            </div>
            <p className="mt-1 text-sm">{step.explanation}</p>
          </li>
        ))}
      </ol>
    </div>
  );
}
