import { cn } from "@/lib/utils";

export function StatTile({ label, value, hint, className }: { label: string; value: number | string; hint?: string; className?: string }) {
  return (
    <div className={cn("rounded-lg border border-line bg-panel p-3", className)}>
      <div className="font-sign text-xs tracking-widest text-muted uppercase">{label}</div>
      <div className="font-log text-2xl font-semibold tabular-nums">{value}</div>
      {hint && <div className="text-xs text-muted">{hint}</div>}
    </div>
  );
}
