import { NavLink, Outlet } from "react-router";
import { useSandbox, useApprovals, useProviders, useStats } from "@/api/hooks";
import { Ticker } from "./Ticker";
import { usePrefs } from "@/lib/prefs";
import { cn, num } from "@/lib/utils";

const nav = [
  ["/", "Overview"],
  ["/messages", "Messages"],
  ["/approvals", "Approvals"],
  ["/suppressions", "Suppressions"],
  ["/providers", "Providers"],
  ["/chaos", "Chaos run"],
  ["/settings", "Settings"],
] as const;

export function Logo({ className }: { className?: string }) {
  return (
    <span className={cn("inline-flex items-center gap-2 font-sign text-3xl leading-none font-extrabold uppercase", className)}>
      <svg viewBox="0 0 32 32" className="size-7" aria-hidden="true">
        <rect width="32" height="32" fill="currentColor" />
        <g fill="none" stroke="var(--paper)" strokeWidth="2">
          <rect x="6" y="6" width="9" height="9" />
          <rect x="17" y="6" width="9" height="9" />
          <rect x="6" y="17" width="9" height="9" />
        </g>
        <rect x="17" y="17" width="9" height="9" fill="var(--wrap-pink)" />
      </svg>
      Pigeonhole
    </span>
  );
}

export function Layout() {
  const sandbox = useSandbox();
  const approvals = useApprovals();
  const { motion, setMotion, theme, setTheme } = usePrefs();
  const pending = approvals.data?.length ?? 0;
  const stats = useStats().data;
  const providers = useProviders().data ?? [];
  const sent = num(stats?.byStatus?.Sent) + num(stats?.byStatus?.Delivered);
  const facts = [
    `${num(stats?.acceptedLast5Min).toLocaleString()} accepted in 5 min`,
    `${sent.toLocaleString()} sent or delivered`,
    `${num(stats?.outboxDepth).toLocaleString()} waiting in the outbox`,
    pending ? `${pending} held for your decision` : "nobody waiting on you",
    ...providers.map((p) => `${p.name} ${p.circuitState === "Open" ? "tripped" : p.circuitState === "HalfOpen" ? "probing" : "healthy"}`),
    `${num(stats?.quotaUsed).toLocaleString()} of ${num(stats?.dailyQuota).toLocaleString()} quota used`,
  ];
  return (
    <div className="min-h-screen">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:p-2">
        Skip to content
      </a>
      {sandbox.data?.enabled && (
        <div className="bg-ink px-4 py-2 text-center text-sm text-paper" role="note">
          Sandbox: mail goes to a test inbox, nothing is delivered.
          {sandbox.data.mailpitUrl && (
            <>
              {" "}
              <a className="font-bold underline underline-offset-4" href={sandbox.data.mailpitUrl} target="_blank" rel="noreferrer">
                Open the test inbox
              </a>
            </>
          )}
        </div>
      )}
      <header className="border-b-2 border-ink bg-paper">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-8 gap-y-3 px-4 py-4">
          <Logo />
          <nav aria-label="Main" className="flex flex-wrap gap-x-4 gap-y-1">
            {nav.map(([to, label]) => (
              <NavLink
                key={to}
                to={to}
                end={to === "/"}
                className={({ isActive }) =>
                  cn(
                    "border-b-[3px] py-1 font-sign text-base font-bold tracking-wide uppercase hover:text-hover",
                    isActive ? "border-ink" : "border-transparent",
                  )
                }
              >
                {label}
                {to === "/approvals" && pending > 0 && <span className="ml-1 bg-wrap-yellow px-1.5 text-sm text-on-wrap">{pending}</span>}
              </NavLink>
            ))}
          </nav>
          <div className="ml-auto flex items-center gap-4 text-sm">
            <a href="/docs" className="font-bold underline underline-offset-4 hover:text-hover">
              API docs
            </a>
            <label className="flex items-center gap-1">
              <input type="checkbox" className="accent-[var(--hover)]" checked={motion} onChange={(e) => setMotion(e.target.checked)} />{" "}
              Motion
            </label>
            <label className="flex items-center gap-1">
              <span className="sr-only">Theme</span>
              <select
                value={theme}
                onChange={(e) => setTheme(e.target.value as typeof theme)}
                className="border-2 border-ink bg-paper px-1 py-1"
              >
                <option value="system">System theme</option>
                <option value="light">Light</option>
                <option value="dark">Dark</option>
              </select>
            </label>
          </div>
        </div>
      </header>
      <Ticker items={facts} />
      <main id="main" className="mx-auto max-w-6xl px-4 py-8">
        <Outlet />
      </main>
    </div>
  );
}

export const PageTitle = ({ children, lead }: { children: string; lead?: string }) => (
  <div className="rise mb-6">
    <h1 className="font-sign text-5xl leading-none font-extrabold uppercase md:text-6xl">{children}</h1>
    {lead && <p className="mt-2 max-w-2xl text-lg text-muted">{lead}</p>}
  </div>
);
