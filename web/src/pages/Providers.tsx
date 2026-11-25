import { useProviders, useSandbox, useSwitches } from "@/api/hooks";
import { PageTitle } from "@/components/Layout";
import { Card } from "@/components/ui/card";
import { cn, num } from "@/lib/utils";

const circuit = {
  Closed: { word: "Healthy (circuit closed)", tone: "text-good" },
  HalfOpen: { word: "Probing (half-open)", tone: "text-warn" },
  Open: { word: "Tripped (circuit open)", tone: "text-bad" },
  Isolated: { word: "Isolated", tone: "text-bad" },
} as const;

export default function Providers() {
  const providers = useProviders();
  const sandbox = useSandbox();
  const switches = useSwitches(!!sandbox.data?.enabled);
  const items = providers.data ?? [];
  return (
    <>
      <PageTitle lead="Failover order, circuit state and error rate over the last 5 minutes, reported by the workers.">Providers</PageTitle>
      <div className="grid gap-4 md:grid-cols-2">
        {items.map((p) => {
          const attempts = num(p.attempts);
          const failures = num(p.failures);
          const off = switches.data?.downProviders.includes(p.name ?? "");
          const c = circuit[(p.circuitState ?? "Closed") as keyof typeof circuit] ?? circuit.Closed;
          return (
            <Card key={p.name}>
              <div className="flex items-baseline justify-between">
                <h2 className="font-sign text-xl uppercase">
                  {num(p.position) + 1}. {p.name}
                </h2>
                <span className={cn("text-sm font-medium", off ? "text-bad" : c.tone)}>{off ? "Switched off (sandbox)" : c.word}</span>
              </div>
              <dl className="mt-3 grid grid-cols-3 gap-2 text-sm">
                <div>
                  <dt className="text-muted">Attempts</dt>
                  <dd className="font-log text-lg">{attempts}</dd>
                </div>
                <div>
                  <dt className="text-muted">Failures</dt>
                  <dd className="font-log text-lg">{failures}</dd>
                </div>
                <div>
                  <dt className="text-muted">Error rate</dt>
                  <dd className="font-log text-lg">{attempts ? `${Math.round((failures / attempts) * 100)}%` : "–"}</dd>
                </div>
              </dl>
              {p.lastError && <p className="mt-2 font-log text-xs text-muted">Last error: {p.lastError}</p>}
              <p className="mt-2 text-xs text-muted">Updated {p.updatedAt ? new Date(p.updatedAt).toLocaleTimeString() : "never"}</p>
            </Card>
          );
        })}
        {items.length === 0 && <p className="text-muted">No worker has reported yet. Start one with <code className="font-log">dotnet run --project src/Relay.Worker</code>.</p>}
      </div>
      <p className="mt-4 text-sm text-muted">Failover order comes from <code className="font-log">Relay:Providers</code> in the worker's configuration.</p>
    </>
  );
}
