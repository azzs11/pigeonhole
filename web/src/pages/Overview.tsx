import { usePromises, useProviders, useSandbox, useStats, useSwitches } from "@/api/hooks";
import { SortingOffice } from "@/components/SortingOffice";
import { PromisesPanel } from "@/components/PromisesPanel";
import { SabotagePanel } from "@/components/SabotagePanel";
import { StatTile } from "@/components/StatTile";
import { PageTitle } from "@/components/Layout";
import { usePrefs } from "@/lib/prefs";
import { num } from "@/lib/utils";

export default function Overview() {
  const stats = useStats();
  const providers = useProviders();
  const sandbox = useSandbox();
  const enabled = !!sandbox.data?.enabled;
  const switches = useSwitches(enabled);
  const promises = usePromises(enabled);
  const { motion } = usePrefs();
  const s = stats.data;
  return (
    <>
      <PageTitle lead="Every message has a visible life story. Every held or failed one says why.">Sorting office</PageTitle>
      {s?.pausedReason && (
        <p role="alert" className="mb-4 rounded-md border-l-4 border-bad bg-bad-bg p-3 text-bad">
          Sending is paused: {s.pausedReason}
        </p>
      )}
      <SortingOffice stats={s} providers={providers.data} down={switches.data?.downProviders ?? []} motion={motion} />
      <div className="mt-4 grid grid-cols-2 gap-3 md:grid-cols-4">
        <StatTile label="Accepted (5 min)" value={num(s?.acceptedLast5Min)} />
        <StatTile label="Sent (5 min)" value={num(s?.sentLast5Min)} />
        <StatTile label="Waiting in outbox" value={num(s?.outboxDepth)} />
        <StatTile label="Quota today" value={`${num(s?.quotaUsed)} / ${num(s?.dailyQuota)}`} />
      </div>
      {enabled && (
        <div className="mt-6 grid gap-4 md:grid-cols-2">
          <SabotagePanel providers={providers.data ?? []} switches={switches.data} />
          <PromisesPanel promises={promises.data} />
        </div>
      )}
    </>
  );
}
