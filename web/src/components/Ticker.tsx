import type { ReactNode } from "react";

/**
 * A strip of facts running across the page, like the band on a chocolate box. The list is
 * rendered twice so the loop is seamless; screen readers get the first copy only.
 */
export function Ticker({ items, className = "bg-wrap-yellow" }: { items: ReactNode[]; className?: string }) {
  const run = (hidden: boolean) => (
    <ul className="flex shrink-0 items-center" aria-hidden={hidden || undefined}>
      {items.map((item, i) => (
        <li key={i} className="flex items-center gap-6 pr-6 font-sign text-lg font-bold tracking-wide whitespace-nowrap uppercase">
          {item}
          <span aria-hidden="true" className="inline-block size-2.5 bg-current" />
        </li>
      ))}
    </ul>
  );
  return (
    <div className={`overflow-hidden border-b-2 border-ink py-1.5 text-on-wrap ${className}`}>
      <div className="marquee">
        {run(false)}
        {run(true)}
      </div>
    </div>
  );
}
