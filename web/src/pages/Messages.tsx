import { useMemo, useState, type FormEvent } from "react";
import { useSearchParams } from "react-router";
import { useBounce, useExplain, useMessages, useSandbox, useSend } from "@/api/hooks";
import type { MessageStatus } from "@/api/client";
import { StampBadge, statusLook } from "@/components/StampBadge";
import { Timeline } from "@/components/Timeline";
import { PageTitle } from "@/components/Layout";
import { Button } from "@/components/ui/button";
import { Card, CardTitle } from "@/components/ui/card";
import { Input, Label } from "@/components/ui/input";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";

function SendForm() {
  const send = useSend();
  const [form, setForm] = useState({ to: "", subject: "", text: "" });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    send.mutate(
      {
        to: form.to
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean),
        subject: form.subject,
        text: form.text,
      },
      { onSuccess: () => setForm({ to: "", subject: "", text: "" }) },
    );
  };
  return (
    <Card className="mb-6">
      <CardTitle>Send a message</CardTitle>
      <form onSubmit={submit} className="mt-3 grid gap-3 md:grid-cols-3">
        <div>
          <Label htmlFor="to">To</Label>
          <Input
            id="to"
            value={form.to}
            onChange={(e) => setForm({ ...form, to: e.target.value })}
            placeholder="someone@example.test"
            required
          />
        </div>
        <div>
          <Label htmlFor="subject">Subject</Label>
          <Input id="subject" value={form.subject} onChange={(e) => setForm({ ...form, subject: e.target.value })} required />
        </div>
        <div>
          <Label htmlFor="text">Text</Label>
          <Input id="text" value={form.text} onChange={(e) => setForm({ ...form, text: e.target.value })} required />
        </div>
        <div className="md:col-span-3">
          <Button type="submit" disabled={send.isPending}>
            Send
          </Button>
          {send.isError && (
            <span role="alert" className="ml-3 text-sm text-bad">
              {send.error.message}
            </span>
          )}
        </div>
      </form>
    </Card>
  );
}

function MessagePanel({ id, onClose }: { id: string | null; onClose: () => void }) {
  const explain = useExplain(id);
  const sandbox = useSandbox();
  const bounce = useBounce();
  const status = explain.data?.status;
  return (
    <Dialog open={!!id} onOpenChange={(open) => !open && onClose()}>
      <DialogContent side aria-describedby="message-id">
        <DialogTitle>Message timeline</DialogTitle>
        <DialogDescription id="message-id" className="mt-1 flex items-center gap-2 font-log text-xs text-muted">
          {id}
          <button className="underline" onClick={() => id && navigator.clipboard?.writeText(id)}>
            copy
          </button>
        </DialogDescription>
        <div className="mt-4">{explain.data ? <Timeline explanation={explain.data} /> : <p>Loading…</p>}</div>
        {sandbox.data?.enabled && (status === "Sent" || status === "Delivered") && (
          <div className="mt-6 border-t border-line pt-4">
            <Button variant="outline" onClick={() => id && bounce.mutate(id)} disabled={bounce.isPending}>
              Simulate a bounce
            </Button>
            <p className="mt-1 text-xs text-muted">
              Plays the provider's hard-bounce webhook. You should see Returned, and the address on Suppressions.
            </p>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

export default function Messages() {
  const messages = useMessages();
  const [params, setParams] = useSearchParams();
  const filter = params.get("status") as MessageStatus | null;
  const open = params.get("id");
  const items = useMemo(() => (messages.data?.items ?? []).filter((m) => !filter || m.status === filter), [messages.data, filter]);
  const set = (key: string, value: string | null) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next);
  };
  return (
    <>
      <PageTitle lead="Find a message and see what happened to it.">Messages</PageTitle>
      <SendForm />
      <label className="mb-3 flex items-center gap-2 text-sm">
        Show
        <select
          className="min-h-11 border-2 border-ink bg-panel px-2"
          value={filter ?? ""}
          onChange={(e) => set("status", e.target.value || null)}
        >
          <option value="">all states</option>
          {Object.entries(statusLook).map(([k, v]) => (
            <option key={k} value={k}>
              {v.label}
            </option>
          ))}
        </select>
      </label>
      <div className="overflow-x-auto border-2 border-ink bg-panel">
        <table className="w-full text-sm">
          <caption className="sr-only">Messages, newest first</caption>
          <thead className="border-b-2 border-ink bg-ink text-left font-sign text-sm tracking-wider text-paper uppercase">
            <tr>
              <th className="p-2">State</th>
              <th className="p-2">To</th>
              <th className="p-2">Subject</th>
              <th className="p-2">Accepted</th>
            </tr>
          </thead>
          <tbody>
            {items.map((m) => (
              <tr key={m.id} className="border-b border-line last:border-0 hover:bg-paper">
                <td className="p-2">
                  <StampBadge status={m.status} />
                </td>
                <td className="p-2 font-log text-xs">{m.to.join(", ")}</td>
                <td className="p-2">
                  <button className="text-left underline" onClick={() => set("id", m.id)}>
                    {m.subject}
                  </button>
                </td>
                <td className="p-2 font-log text-xs">{new Date(m.createdAt).toLocaleTimeString()}</td>
              </tr>
            ))}
            {items.length === 0 && (
              <tr>
                <td colSpan={4} className="p-6 text-center text-muted">
                  No messages yet. Send one above, or run a test batch from the Overview.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      <MessagePanel id={open} onClose={() => set("id", null)} />
    </>
  );
}
