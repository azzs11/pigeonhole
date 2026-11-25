import results from "../../../bench/results/mailchaos/results.json";
import { usePromises, useSandbox, useStats } from "@/api/hooks";
import { LineChart } from "@/components/LineChart";
import { PromisesPanel } from "@/components/PromisesPanel";
import { StatTile } from "@/components/StatTile";
import { PageTitle } from "@/components/Layout";
import { Card, CardTitle } from "@/components/ui/card";
import { num } from "@/lib/utils";

type Run = {
  system: string;
  faultRate: number;
  duplicatesPer10k: number;
  lostPer10k: number;
};
const colors: Record<string, string> = {
  "naive send-then-save": "var(--bad)",
  "outbox only": "var(--warn)",
  "outbox + unknown state": "var(--good)",
};

export default function ChaosRun() {
  const runs = results.runs as Run[];
  const systems = [...new Set(runs.map((r) => r.system))];
  const series = (metric: "duplicatesPer10k" | "lostPer10k") =>
    systems.map((s) => ({
      name: s,
      color: colors[s] ?? "var(--accent)",
      points: runs.filter((r) => r.system === s).map((r) => ({ x: r.faultRate * 100, y: r[metric] })),
    }));
  const sandbox = useSandbox();
  const promises = usePromises(!!sandbox.data?.enabled);
  const stats = useStats();
  const by = (k: string) => num(stats.data?.byStatus?.[k]);
  return (
    <>
      <PageTitle lead="The reliability claim, tested: the last MailChaos run, and the live sandbox.">Chaos run</PageTitle>
      <Card>
        <CardTitle>MailChaos: three systems, same faults</CardTitle>
        <p className="mt-1 text-sm text-muted">
          {results.messagesPerRun.toLocaleString()} messages per run, seed {results.seed}, commit{" "}
          <code className="font-log">{results.commit}</code>. Rerun with <code className="font-log">bench/run-mailchaos.sh</code>.
        </p>
        <div className="mt-4 grid gap-6 md:grid-cols-2">
          <LineChart title="Duplicate sends per 10,000" series={series("duplicatesPer10k")} xLabel="injected fault rate (%)" />
          <LineChart title="Lost messages per 10,000" series={series("lostPer10k")} xLabel="injected fault rate (%)" />
        </div>
        <table className="mt-4 w-full text-sm">
          <caption className="sr-only">MailChaos results: duplicates and losses per 10,000 at each fault rate</caption>
          <thead className="border-b-2 border-ink bg-ink text-left font-sign text-sm tracking-wider text-paper uppercase">
            <tr>
              <th className="p-1">System</th>
              {results.faultRates.map((f: number) => (
                <th key={f} className="p-1 font-log">
                  {f * 100}%
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {systems.map((s) => (
              <tr key={s} className="border-b border-line last:border-0">
                <th scope="row" className="p-1 text-left font-normal">
                  {s}
                </th>
                {runs
                  .filter((r) => r.system === s)
                  .map((r) => (
                    <td key={r.faultRate} className="p-1 font-log text-xs">
                      {Math.round(r.duplicatesPer10k)} dup / {Math.round(r.lostPer10k)} lost
                    </td>
                  ))}
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
      {sandbox.data?.enabled && (
        <div className="mt-6 grid gap-4 md:grid-cols-2">
          <div className="grid grid-cols-2 gap-3">
            <StatTile wrap="bg-wrap-mint" label="Delivered or sent" value={by("Delivered") + by("Sent")} />
            <StatTile wrap="bg-wrap-yellow" label="Unknown (held, not resent)" value={by("Unknown")} />
            <StatTile wrap="bg-wrap-blue" label="Waiting in outbox" value={num(stats.data?.outboxDepth)} />
            <StatTile wrap="bg-wrap-pink" label="Returned" value={by("Bounced")} />
          </div>
          <PromisesPanel promises={promises.data} />
        </div>
      )}
    </>
  );
}
