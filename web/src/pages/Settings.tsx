import { useQueryClient } from "@tanstack/react-query";
import { KEY_STORAGE } from "@/api/client";
import { useStats } from "@/api/hooks";
import { PageTitle } from "@/components/Layout";
import { Button } from "@/components/ui/button";
import { Card, CardTitle } from "@/components/ui/card";
import { num, storage } from "@/lib/utils";

export default function Settings() {
  const stats = useStats();
  const qc = useQueryClient();
  const key = storage.get(KEY_STORAGE) ?? "";
  const used = num(stats.data?.quotaUsed);
  const limit = num(stats.data?.dailyQuota);
  return (
    <>
      <PageTitle lead="Keys, quota and policy for this tenant.">Settings</PageTitle>
      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <CardTitle>API key</CardTitle>
          <p className="mt-2 text-sm">
            Signed in with <code className="font-log">{key.slice(0, 12)}…</code>
          </p>
          <p className="mt-2 text-xs text-muted">
            Keys are created and revoked with the CLI (<code className="font-log">relay keys create | revoke</code>
            ), shown once and stored hashed. Rotate by creating a new key, then revoking the old one.
          </p>
          <Button
            className="mt-3"
            variant="outline"
            onClick={() => {
              storage.set(KEY_STORAGE, null);
              qc.clear();
              location.assign("/");
            }}
          >
            Sign out
          </Button>
        </Card>
        <Card>
          <CardTitle>Daily quota</CardTitle>
          <p className="mt-2 text-sm">
            {used.toLocaleString()} of {limit.toLocaleString()} recipients used today (resets 00:00 UTC).
          </p>
          <div
            className="mt-2 h-4 w-full border-2 border-ink bg-paper"
            role="progressbar"
            aria-label="Quota used"
            aria-valuemin={0}
            aria-valuemax={limit}
            aria-valuenow={used}
          >
            <div
              className="h-full bg-hover"
              style={{
                width: `${limit ? Math.min(100, (used / limit) * 100) : 0}%`,
              }}
            />
          </div>
          {stats.data?.pausedReason && <p className="mt-3 text-sm text-bad">Paused: {stats.data.pausedReason}</p>}
          <p className="mt-3 text-xs text-muted">
            Change it with <code className="font-log">relay tenants quota --tenant NAME --daily N</code>.
          </p>
        </Card>
      </div>
    </>
  );
}
