import type { Schemas } from "@/api/client";
import { Card, CardTitle } from "./ui/card";
import { cn } from "@/lib/utils";

/** The three delivery promises, checked live and stated as sentences. A broken promise turns red. */
export function PromisesPanel({ promises }: { promises: Schemas["Promise"][] | undefined }) {
  return (
    <Card>
      <CardTitle>Promises</CardTitle>
      <ul className="mt-3 space-y-2" aria-live="polite">
        {(promises ?? []).map((p) => (
          <li
            key={p.name}
            className={cn("rounded-md border-l-4 p-2", p.kept ? "border-good bg-good-bg" : "border-bad bg-bad-bg")}
          >
            <div className={cn("font-medium", p.kept ? "text-good" : "text-bad")}>
              {p.kept ? "Kept" : "Broken"}: {p.name}
            </div>
            <div className="text-sm">{p.detail}</div>
          </li>
        ))}
        {!promises && <li className="text-sm text-muted">Checking…</li>}
      </ul>
    </Card>
  );
}
