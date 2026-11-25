import type { CSSProperties } from "react";
import { cn } from "@/lib/utils";
import { useCountUp } from "@/lib/useCountUp";

/** Decorative wrapper colours; they never carry status, so any tile may wear any of them. */
export const wrappers = ["bg-wrap-pink", "bg-wrap-yellow", "bg-wrap-blue", "bg-wrap-mint", "bg-wrap-orange", "bg-wrap-lilac"] as const;

const Num = ({ value }: { value: number }) => <>{useCountUp(value).toLocaleString()}</>;

export function StatTile({
  label,
  value,
  of,
  hint,
  wrap,
  tilt,
  className,
}: {
  label: string;
  value: number | string;
  /** Renders "value / of" with both numbers counting. */
  of?: number;
  hint?: string;
  wrap?: (typeof wrappers)[number];
  tilt?: string;
  className?: string;
}) {
  return (
    <div
      className={cn("lift border-2 border-ink p-4", wrap ? `${wrap} text-on-wrap` : "bg-panel", className)}
      style={{ "--tilt": tilt } as CSSProperties}
    >
      <div className="font-sign text-sm font-bold tracking-widest uppercase">{label}</div>
      <div className="mt-1 font-sign text-5xl leading-none font-extrabold tabular-nums">
        {typeof value === "number" ? <Num value={value} /> : value}
        {of !== undefined && (
          <span className="text-3xl">
            {" / "}
            <Num value={of} />
          </span>
        )}
      </div>
      {hint && <div className="mt-1 text-xs">{hint}</div>}
    </div>
  );
}
