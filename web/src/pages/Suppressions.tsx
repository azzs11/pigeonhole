import { useState } from "react";
import { useRemoveSuppression, useSuppressions } from "@/api/hooks";
import { PageTitle } from "@/components/Layout";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";

const reasonWord = {
  HardBounce: "Hard bounce",
  Complaint: "Complaint",
  Manual: "Added by hand",
} as const;

export default function Suppressions() {
  const list = useSuppressions();
  const remove = useRemoveSuppression();
  const [confirm, setConfirm] = useState<string | null>(null);
  const items = list.data?.items ?? [];
  return (
    <>
      <PageTitle lead="Addresses this tenant will not send to, and why.">Suppressions</PageTitle>
      <div className="overflow-x-auto border-2 border-ink bg-panel">
        <table className="w-full text-sm">
          <caption className="sr-only">Suppressed addresses</caption>
          <thead className="border-b-2 border-ink bg-ink text-left font-sign text-sm tracking-wider text-paper uppercase">
            <tr>
              <th className="p-2">Address</th>
              <th className="p-2">Reason</th>
              <th className="p-2">Since</th>
              <th className="p-2">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {items.map((s) => (
              <tr key={s.address} className="border-b border-line last:border-0">
                <td className="p-2 font-log text-xs">{s.address}</td>
                <td className="p-2">{reasonWord[s.reason]}</td>
                <td className="p-2 font-log text-xs">{new Date(s.createdAt).toLocaleString()}</td>
                <td className="p-2 text-right">
                  <Button size="sm" variant="outline" onClick={() => setConfirm(s.address)}>
                    Remove
                  </Button>
                </td>
              </tr>
            ))}
            {items.length === 0 && (
              <tr>
                <td colSpan={4} className="p-6 text-center text-muted">
                  No suppressed addresses. Hard bounces and complaints land here automatically.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      <Dialog open={!!confirm} onOpenChange={(o) => !o && setConfirm(null)}>
        <DialogContent>
          <DialogTitle>Remove suppression?</DialogTitle>
          <DialogDescription className="mt-2 text-sm">
            Mail to <span className="font-log">{confirm}</span> will be accepted again. If the address still bounces, it will come back.
          </DialogDescription>
          <div className="mt-5 flex justify-end gap-2">
            <DialogClose asChild>
              <Button variant="outline">Keep it</Button>
            </DialogClose>
            <Button
              variant="bad"
              onClick={() =>
                confirm &&
                remove.mutate(confirm, {
                  onSuccess: () => setConfirm(null),
                })
              }
            >
              Remove
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
