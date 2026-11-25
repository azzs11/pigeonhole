import { useState, type FormEvent, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { KEY_STORAGE } from "@/api/client";
import { storage } from "@/lib/utils";
import { Button } from "./ui/button";
import { Input, Label } from "./ui/input";
import { Card } from "./ui/card";

/** The dashboard acts as an API key holder. The key lives in this tab's session storage only. */
export function KeyGate({ children }: { children: ReactNode }) {
  const [key, setKey] = useState(() => storage.get(KEY_STORAGE));
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);
  const qc = useQueryClient();

  if (key) return <>{children}</>;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    const r = await fetch("/v1/stats", { headers: { Authorization: `Bearer ${draft.trim()}` } });
    if (!r.ok) {
      setError(r.status === 401 ? "That key was not accepted." : `The API answered ${r.status}.`);
      return;
    }
    storage.set(KEY_STORAGE, draft.trim());
    qc.clear();
    setKey(draft.trim());
  };

  return (
    <main className="mx-auto mt-16 max-w-md px-4">
      <Card>
        <h1 className="font-sign text-2xl tracking-wide uppercase">Pigeonhole</h1>
        <p className="mt-1 text-sm text-muted">A sorting office for email you can watch, and sabotage.</p>
        <form onSubmit={submit} className="mt-6 space-y-3">
          <Label htmlFor="key">API key</Label>
          <Input id="key" type="password" autoComplete="off" value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="rly_…" required />
          <p className="text-xs text-muted">
            Create one with <code className="font-log">relay keys create --tenant demo</code>. It is kept only in this browser tab.
          </p>
          {error && (
            <p role="alert" className="text-sm text-bad">
              {error}
            </p>
          )}
          <Button type="submit" className="w-full">
            Open the sorting office
          </Button>
        </form>
      </Card>
    </main>
  );
}
