import { useBatch, useSetSwitches } from "@/api/hooks";
import type { Schemas } from "@/api/client";
import { Card, CardTitle } from "./ui/card";
import { Button } from "./ui/button";
import { num } from "@/lib/utils";

type Props = {
  providers: Schemas["ProviderHealth"][];
  switches: Schemas["SwitchesDto"] | undefined;
};

/** Sandbox only. Play first, explain after: every switch says what you should see. */
export function SabotagePanel({ providers, switches }: Props) {
  const set = useSetSwitches();
  const batch = useBatch();
  const current = switches ?? {
    downProviders: [],
    delayMilliseconds: 0,
    crashRate: 0,
  };
  const update = (patch: Partial<Schemas["SwitchesDto"]>) => set.mutate({ ...current, ...patch });

  return (
    <Card>
      <CardTitle>Sabotage panel</CardTitle>
      <p className="mt-1 text-sm text-muted">Break things and watch the promises hold.</p>
      <div className="mt-4 space-y-4">
        <div>
          <Button onClick={() => batch.mutate(200)} disabled={batch.isPending}>
            Send a test batch (200)
          </Button>
          <p className="mt-1 text-xs text-muted">You should see: Intake fill up, then drain into Delivered within seconds.</p>
          {batch.isSuccess && (
            <p className="text-sm text-good" role="status">
              Batch accepted.
            </p>
          )}
        </div>
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">Kill a provider</legend>
          {providers.map((p) => {
            const name = p.name ?? "";
            const off = current.downProviders.includes(name);
            return (
              <label key={name} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  className="size-5"
                  checked={off}
                  onChange={() =>
                    update({
                      downProviders: off ? current.downProviders.filter((d) => d !== name) : [...current.downProviders, name],
                    })
                  }
                />
                Switch off <strong>{name}</strong>
              </label>
            );
          })}
          <p className="text-xs text-muted">
            You should see: traffic move to the next provider; with all off, mail waits in Sorting and none is lost.
          </p>
        </fieldset>
        <label className="block text-sm">
          <span className="font-medium">Slow every provider call</span>
          <select
            className="mt-1 block min-h-10 border-2 border-ink bg-panel px-2"
            value={num(current.delayMilliseconds)}
            onChange={(e) =>
              update({
                delayMilliseconds: Number(e.target.value),
              })
            }
          >
            <option value={0}>No delay</option>
            <option value={500}>500 ms</option>
            <option value={2000}>2 s</option>
          </select>
        </label>
        <label className="block text-sm">
          <span className="font-medium">Crash the worker after the provider accepts</span>
          <select
            className="mt-1 block min-h-10 border-2 border-ink bg-panel px-2"
            value={num(current.crashRate)}
            onChange={(e) => update({ crashRate: Number(e.target.value) })}
          >
            <option value={0}>Never</option>
            <option value={0.05}>5% of sends</option>
            <option value={0.2}>20% of sends</option>
          </select>
          <span className="mt-1 block text-xs text-muted">
            You should see: some mail in Unknown instead of being sent twice. "Nothing sent twice" stays green.
          </span>
        </label>
      </div>
    </Card>
  );
}
