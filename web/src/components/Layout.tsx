import { NavLink, Outlet } from "react-router";
import { useSandbox, useApprovals } from "@/api/hooks";
import { usePrefs } from "@/lib/prefs";
import { cn } from "@/lib/utils";

const nav = [
  ["/", "Overview"],
  ["/messages", "Messages"],
  ["/approvals", "Approvals"],
  ["/suppressions", "Suppressions"],
  ["/providers", "Providers"],
  ["/chaos", "Chaos run"],
  ["/settings", "Settings"],
] as const;

export function Layout() {
  const sandbox = useSandbox();
  const approvals = useApprovals();
  const { motion, setMotion, theme, setTheme } = usePrefs();
  const pending = approvals.data?.length ?? 0;
  return (
    <div className="min-h-screen">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:p-2">
        Skip to content
      </a>
      {sandbox.data?.enabled && (
        <div className="bg-warn-bg px-4 py-1.5 text-center text-sm text-warn" role="note">
          Sandbox: mail goes to a test inbox, nothing is delivered.
          {sandbox.data.mailpitUrl && (
            <>
              {" "}
              <a className="underline" href={sandbox.data.mailpitUrl} target="_blank" rel="noreferrer">
                Open the test inbox
              </a>
            </>
          )}
        </div>
      )}
      <header className="border-b border-line bg-panel">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
          <span className="font-sign text-xl tracking-wider uppercase">Pigeonhole</span>
          <nav aria-label="Main" className="flex flex-wrap gap-1">
            {nav.map(([to, label]) => (
              <NavLink
                key={to}
                to={to}
                end={to === "/"}
                className={({ isActive }) => cn("rounded px-2 py-1.5 text-sm", isActive ? "bg-ink text-paper" : "hover:bg-paper")}
              >
                {label}
                {to === "/approvals" && pending > 0 && <span className="ml-1 rounded bg-warn-bg px-1 text-xs text-warn">{pending}</span>}
              </NavLink>
            ))}
          </nav>
          <div className="ml-auto flex items-center gap-3 text-sm">
            <a href="/docs" className="underline">
              API docs
            </a>
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={motion} onChange={(e) => setMotion(e.target.checked)} /> Motion
            </label>
            <label className="flex items-center gap-1">
              <span className="sr-only">Theme</span>
              <select value={theme} onChange={(e) => setTheme(e.target.value as typeof theme)} className="rounded border border-line bg-panel px-1 py-1">
                <option value="system">System theme</option>
                <option value="light">Light</option>
                <option value="dark">Dark</option>
              </select>
            </label>
          </div>
        </div>
      </header>
      <main id="main" className="mx-auto max-w-6xl px-4 py-6">
        <Outlet />
      </main>
    </div>
  );
}

export const PageTitle = ({ children, lead }: { children: string; lead?: string }) => (
  <div className="mb-5">
    <h1 className="font-sign text-3xl tracking-wide uppercase">{children}</h1>
    {lead && <p className="text-muted">{lead}</p>}
  </div>
);
