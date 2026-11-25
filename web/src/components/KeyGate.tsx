import { useState, type CSSProperties, type FormEvent, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { KEY_STORAGE } from "@/api/client";
import { storage } from "@/lib/utils";
import { Button } from "./ui/button";
import { Input, Label } from "./ui/input";
import { Logo } from "./Layout";
import { PigeonholeWall } from "./PigeonholeWall";
import { Ticker } from "./Ticker";

/** The dashboard acts as an API key holder. The key lives in this tab's session storage only. */
export function KeyGate({ children }: { children: ReactNode }) {
  const [key, setKey] = useState(() => storage.get(KEY_STORAGE));
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const qc = useQueryClient();
  const sandbox = useQuery({
    queryKey: ["sandbox-public"],
    queryFn: async () => {
      const r = await fetch("/v1/sandbox");
      return r.ok ? ((await r.json()) as { enabled: boolean }) : { enabled: false };
    },
    enabled: !key,
  });

  if (key) return <>{children}</>;

  const enter = (value: string) => {
    storage.set(KEY_STORAGE, value);
    qc.clear();
    setKey(value);
  };

  const tryDemo = async () => {
    setError(null);
    setBusy(true);
    try {
      const r = await fetch("/v1/sandbox/demo-key", { method: "POST" });
      if (!r.ok) {
        setError(r.status === 429 ? "Too many demo keys from this address; try again in an hour." : `The API answered ${r.status}.`);
        return;
      }
      enter(((await r.json()) as { key: string }).key);
    } finally {
      setBusy(false);
    }
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    const r = await fetch("/v1/stats", {
      headers: { Authorization: `Bearer ${draft.trim()}` },
    });
    if (!r.ok) {
      setError(r.status === 401 ? "That key was not accepted." : `The API answered ${r.status}.`);
      return;
    }
    enter(draft.trim());
  };

  return (
    <div className="flex min-h-screen flex-col">
      <header className="border-b-2 border-ink">
        <div className="mx-auto flex w-full max-w-6xl items-center px-4 py-4">
          <Logo />
          <a href="/docs" className="ml-auto text-sm font-bold underline underline-offset-4 hover:text-hover">
            API docs
          </a>
        </div>
      </header>
      <Ticker
        className="bg-wrap-pink"
        items={[
          "Sent once, even if a worker dies mid-send",
          "Saved before we say yes",
          "Agents send inside your rules",
          "Pull the plug on a provider, go on",
          "No real email leaves the sandbox",
        ]}
      />
      <main className="mx-auto grid w-full max-w-6xl flex-1 items-center gap-12 px-4 py-12 md:grid-cols-[1.1fr_1fr] md:py-16">
        <div>
          <h1 className="rise font-sign text-6xl leading-[0.88] font-extrabold uppercase md:text-8xl">
            Every letter.
            <br />
            <span className="text-hover">One</span> pigeonhole.
          </h1>
          <p className="rise mt-6 max-w-lg text-lg" style={{ "--i": 1 } as CSSProperties}>
            An email service that doesn&rsquo;t lose mail or send it twice. Here it is running as a little sorting office. Have a go at
            breaking it.
          </p>
          <div className="rise mt-8 max-w-md" style={{ "--i": 2 } as CSSProperties}>
            {sandbox.data?.enabled && (
              <>
                <Button type="button" className="w-full text-lg" onClick={tryDemo} disabled={busy}>
                  {busy ? "Unlocking…" : "Try the sandbox (no key needed)"}
                </Button>
                <p className="mt-2 text-sm text-muted">
                  You get a throwaway key, your own data and 200 sends a day. Mail only reaches the test inbox.
                </p>
              </>
            )}
            <form onSubmit={submit} className="mt-6 border-t-2 border-ink pt-5">
              <Label htmlFor="key">Got an API key?</Label>
              <div className="mt-2 flex gap-2">
                <Input
                  id="key"
                  type="password"
                  autoComplete="off"
                  value={draft}
                  onChange={(e) => setDraft(e.target.value)}
                  placeholder="rly_…"
                  required
                />
                <Button type="submit" variant="outline">
                  Open
                </Button>
              </div>
              <p className="mt-2 text-xs text-muted">
                Make one with <code className="font-log">dotnet run --project tools/Relay.Cli -- keys create --tenant demo</code>. It stays
                in this tab.
              </p>
              {error && (
                <p role="alert" className="mt-2 text-sm font-bold text-bad">
                  {error}
                </p>
              )}
            </form>
          </div>
        </div>
        <div className="rise relative" style={{ "--i": 2 } as CSSProperties}>
          <PigeonholeWall />
          <div className="spin-slow absolute -top-8 -right-4 grid size-28 place-items-center rounded-full border-2 border-ink bg-wrap-yellow text-center font-sign text-base leading-tight font-extrabold text-on-wrap uppercase md:-right-8">
            No real
            <br />
            email.
            <br />
            Ever.
          </div>
        </div>
      </main>
    </div>
  );
}
