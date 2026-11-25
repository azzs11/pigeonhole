import type { CSSProperties } from "react";
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
      <PageTitle lead="Every letter has a life story. Every held or failed one tells you why.">Sorting office</PageTitle>
      {s?.pausedReason && (
        <p role="alert" className="mb-4 border-l-8 border-bad bg-bad-bg p-3 text-bad">
          Sending is paused: {s.pausedReason}
        </p>
      )}
      <div className="rise" style={{ "--i": 1 } as CSSProperties}>
        <SortingOffice stats={s} providers={providers.data} down={switches.data?.downProviders ?? []} motion={motion} />
      </div>
      <div className="rise mt-4 grid grid-cols-2 gap-3 md:grid-cols-4" style={{ "--i": 2 } as CSSProperties}>
        <StatTile wrap="bg-wrap-pink" tilt="-1deg" label="Accepted (5 min)" value={num(s?.acceptedLast5Min)} />
        <StatTile wrap="bg-wrap-yellow" tilt="0.8deg" label="Sent (5 min)" value={num(s?.sentLast5Min)} />
        <StatTile wrap="bg-wrap-blue" tilt="-0.5deg" label="Waiting in outbox" value={num(s?.outboxDepth)} />
        <StatTile wrap="bg-wrap-mint" tilt="1.2deg" label="Quota today" value={num(s?.quotaUsed)} of={num(s?.dailyQuota)} />
      </div>
      {enabled && (
        <div className="rise mt-6 grid gap-4 md:grid-cols-2" style={{ "--i": 3 } as CSSProperties}>
          <SabotagePanel providers={providers.data ?? []} switches={switches.data} />
          <PromisesPanel promises={promises.data} />
        </div>
      )}
    </>
  );
}
