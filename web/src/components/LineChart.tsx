type Series = { name: string; color: string; points: { x: number; y: number }[] };

/** Minimal accessible line chart: the SVG is decorative and a data table carries the numbers. */
export function LineChart({ title, series, xLabel }: { title: string; series: Series[]; xLabel: string }) {
  const W = 420;
  const H = 220;
  const pad = 40;
  const xs = series.flatMap((s) => s.points.map((p) => p.x));
  const ys = series.flatMap((s) => s.points.map((p) => p.y));
  const maxX = Math.max(1, ...xs);
  const maxY = Math.max(1, ...ys);
  const sx = (x: number) => pad + (x / maxX) * (W - pad * 1.5);
  const sy = (y: number) => H - pad + 10 - (y / maxY) * (H - pad * 1.5);
  return (
    <figure>
      <figcaption className="font-sign text-sm tracking-wide uppercase">{title}</figcaption>
      <svg viewBox={`0 0 ${W} ${H}`} className="h-auto w-full" aria-hidden="true">
        <line x1={pad} y1={H - pad + 10} x2={W - 10} y2={H - pad + 10} stroke="var(--line)" />
        <line x1={pad} y1={10} x2={pad} y2={H - pad + 10} stroke="var(--line)" />
        <text x={pad - 6} y={16} textAnchor="end" fontSize="11" fill="var(--muted)">
          {Math.round(maxY)}
        </text>
        <text x={(W + pad) / 2} y={H - 4} textAnchor="middle" fontSize="11" fill="var(--muted)">
          {xLabel}
        </text>
        {series.map((s) => (
          <g key={s.name}>
            <polyline fill="none" stroke={s.color} strokeWidth="2.5" points={s.points.map((p) => `${sx(p.x)},${sy(p.y)}`).join(" ")} />
            {s.points.map((p) => (
              <circle key={p.x} cx={sx(p.x)} cy={sy(p.y)} r="3.5" fill={s.color} />
            ))}
          </g>
        ))}
      </svg>
      <ul className="mt-1 flex flex-wrap gap-3 text-xs">
        {series.map((s) => (
          <li key={s.name} className="flex items-center gap-1">
            <span className="inline-block h-0.5 w-4" style={{ background: s.color }} aria-hidden />
            {s.name}
          </li>
        ))}
      </ul>
    </figure>
  );
}
