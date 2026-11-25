import type { Schemas } from "@/api/client";
import { num } from "@/lib/utils";

type Props = {
  stats: Schemas["TenantStats"] | undefined;
  providers: Schemas["ProviderHealth"][] | undefined;
  down: string[];
  motion: boolean;
};

const circuitWord = (state: string | undefined, down: boolean) =>
  down ? "Switched off" : state === "Open" ? "Tripped" : state === "HalfOpen" ? "Probing" : "Healthy";

/**
 * The home screen: a floor plan of a sorting office. Stations are rooms; envelopes travel the
 * main line at a speed that reflects real throughput (sampled, not one per message). The SVG is
 * decorative; the same facts are in the text summary for screen readers.
 */
export function SortingOffice({ stats, providers, down, motion }: Props) {
  const by = (s: string) => num(stats?.byStatus?.[s]);
  const flow = Math.min(8, Math.ceil(num(stats?.sentLast5Min) / 10));
  const rooms = [
    {
      x: 20,
      label: "Intake",
      count: by("Accepted"),
      note: "accepted",
      wrap: "var(--wrap-pink)",
    },
    {
      x: 205,
      label: "Agent guard",
      count: by("Held"),
      note: "held for approval",
      wrap: "var(--wrap-blue)",
    },
    {
      x: 390,
      label: "Sorting",
      count: num(stats?.outboxDepth),
      note: "in the outbox",
      wrap: "var(--wrap-yellow)",
    },
  ];
  const bins = [
    {
      label: "Delivered",
      count: by("Delivered") + by("Sent"),
      tone: "var(--good)",
    },
    { label: "Returned", count: by("Bounced"), tone: "var(--bad)" },
    { label: "Unknown", count: by("Unknown"), tone: "var(--warn)" },
    {
      label: "Dead letter",
      count: by("DeadLettered") + by("Rejected"),
      tone: "var(--bad)",
    },
  ];
  const list = providers?.length ? providers : [{ name: "no provider reporting", circuitState: "Closed" }];

  return (
    <figure className="border-2 border-ink bg-panel p-3">
      <svg viewBox="0 0 1000 380" className="h-auto w-full" aria-hidden="true">
        <path id="mainline" d="M 30 305 L 970 305" stroke="var(--ink)" strokeWidth="4" strokeDasharray="2 12" fill="none" />
        {rooms.map((r) => (
          <g key={r.label}>
            <rect x={r.x} y={110} width={150} height={160} fill={r.wrap} stroke="var(--ink)" strokeWidth="3" />
            <text
              x={r.x + 75}
              y={140}
              textAnchor="middle"
              fontFamily="Barlow Condensed, sans-serif"
              fontWeight="800"
              fontSize="18"
              fill="var(--on-wrap)"
              letterSpacing="2"
            >
              {r.label.toUpperCase()}
            </text>
            <text
              x={r.x + 75}
              y={205}
              textAnchor="middle"
              fontFamily="Barlow Condensed, sans-serif"
              fontSize="52"
              fontWeight="800"
              fill="var(--on-wrap)"
            >
              {r.count}
            </text>
            <text x={r.x + 75} y={235} textAnchor="middle" fontSize="13" fill="var(--on-wrap)">
              {r.note}
            </text>
          </g>
        ))}
        <g>
          <rect x={575} y={30} width={180} height={320} fill="var(--paper)" stroke="var(--ink)" strokeWidth="3" />
          <text
            x={665}
            y={58}
            textAnchor="middle"
            fontFamily="Barlow Condensed, sans-serif"
            fontWeight="800"
            fontSize="18"
            letterSpacing="2"
            fill="var(--ink)"
          >
            PROVIDERS
          </text>
          {list.slice(0, 4).map((p, i) => {
            const isDown = down.includes(p.name ?? "");
            const word = circuitWord(p.circuitState, isDown);
            const tone = word === "Healthy" ? "var(--good)" : word === "Probing" ? "var(--warn)" : "var(--bad)";
            return (
              <g key={p.name}>
                <rect x={590} y={75 + i * 68} width={150} height={56} fill="var(--panel)" stroke={tone} strokeWidth="3" />
                <text x={665} y={98 + i * 68} textAnchor="middle" fontSize="14" fontWeight="600" fill="var(--ink)">
                  {i + 1}. {p.name}
                </text>
                <text x={665} y={118 + i * 68} textAnchor="middle" fontSize="12" fill={tone}>
                  {word.toUpperCase()}
                </text>
              </g>
            );
          })}
        </g>
        {bins.map((b, i) => (
          <g key={b.label}>
            <rect x={800} y={30 + i * 82} width={180} height={70} fill="var(--paper)" stroke={b.tone} strokeWidth="3" />
            <text
              x={890}
              y={58 + i * 82}
              textAnchor="middle"
              fontFamily="Barlow Condensed, sans-serif"
              fontWeight="800"
              fontSize="15"
              letterSpacing="2"
              fill="var(--ink)"
            >
              {b.label.toUpperCase()}
            </text>
            <text
              x={890}
              y={88 + i * 82}
              textAnchor="middle"
              fontFamily="Barlow Condensed, sans-serif"
              fontSize="30"
              fontWeight="800"
              fill={b.tone}
            >
              {b.count}
            </text>
          </g>
        ))}
        {motion &&
          Array.from({ length: flow }, (_, i) => (
            <g key={i}>
              <rect x={-11} y={-7} width={22} height={14} fill="var(--panel)" stroke="var(--ink)" strokeWidth="1.5" />
              <path d="M -11 -7 L 0 2 L 11 -7" stroke="var(--ink)" strokeWidth="1.5" fill="none" />
              <animateMotion dur="6s" repeatCount="indefinite" begin={`-${(i * 6) / flow}s`}>
                <mpath href="#mainline" />
              </animateMotion>
            </g>
          ))}
      </svg>
      <figcaption className="sr-only">
        <ul>
          {rooms.map((r) => (
            <li key={r.label}>
              {r.label}: {r.count} {r.note}
            </li>
          ))}
          {list.map((p) => (
            <li key={p.name}>
              Provider {p.name}: {circuitWord(p.circuitState, down.includes(p.name ?? ""))}
            </li>
          ))}
          {bins.map((b) => (
            <li key={b.label}>
              {b.label}: {b.count}
            </li>
          ))}
        </ul>
      </figcaption>
    </figure>
  );
}
