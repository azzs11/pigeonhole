import { useApprovals, useDecide } from "@/api/hooks";
import { ApprovalCard } from "@/components/ApprovalCard";
import { PageTitle } from "@/components/Layout";

export default function Approvals() {
  const approvals = useApprovals();
  const decide = useDecide();
  const items = approvals.data ?? [];
  return (
    <>
      <PageTitle lead="Messages the Agent Send Guard held. Nothing here leaves without your decision.">Approvals</PageTitle>
      {decide.isSuccess && (
        <p role="status" className="mb-3 text-sm text-good">
          Decision recorded in the audit trail.
        </p>
      )}
      <div className="space-y-4">
        {items.map((a) => (
          <ApprovalCard key={a.id} approval={a} busy={decide.isPending} onDecide={(approve) => decide.mutate({ id: a.id!, approve })} />
        ))}
        {items.length === 0 && !approvals.isLoading && (
          <p className="rounded-lg border border-dashed border-line p-8 text-center text-muted">Nothing is waiting for approval.</p>
        )}
      </div>
    </>
  );
}
