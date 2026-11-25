import type { CSSProperties } from "react";

const cols = 5;
const rows = 4;
const cell = 64;
const gap = 10;
const wraps = ["var(--wrap-pink)", "var(--wrap-yellow)", "var(--wrap-blue)", "var(--wrap-mint)", "var(--wrap-orange)", "var(--wrap-lilac)"];

// Fixed choreography (no randomness, so renders and screenshots are stable): which slots get a letter,
// where it flies in from, and when.
const letters = [
  { slot: 2, from: [-260, -140], delay: 0 },
  { slot: 6, from: [-300, 60], delay: 0.7 },
  { slot: 13, from: [-240, 200], delay: 1.4 },
  { slot: 9, from: [-320, -40], delay: 2.1 },
  { slot: 17, from: [-280, 160], delay: 2.8 },
  { slot: 0, from: [-220, -200], delay: 3.5 },
  { slot: 11, from: [-300, 20], delay: 4.2 },
];

/** Decorative: a wall of pigeonholes that letters keep flying into, each landing in exactly one slot. */
export function PigeonholeWall() {
  const w = cols * cell + (cols - 1) * gap;
  const h = rows * cell + (rows - 1) * gap;
  const at = (slot: number) => ({
    x: (slot % cols) * (cell + gap),
    y: Math.floor(slot / cols) * (cell + gap),
  });
  return (
    <svg viewBox={`-12 -12 ${w + 24} ${h + 24}`} className="h-auto w-full overflow-visible" aria-hidden="true">
      <rect x={-12} y={-12} width={w + 24} height={h + 24} fill="var(--accent)" />
      {Array.from({ length: cols * rows }, (_, i) => {
        const { x, y } = at(i);
        return <rect key={i} x={x} y={y} width={cell} height={cell} fill="var(--paper)" stroke="var(--ink)" strokeWidth="2" />;
      })}
      {letters.map((l, i) => {
        const { x, y } = at(l.slot);
        return (
          <g key={i} transform={`translate(${x + 10} ${y + 18})`}>
            <g
              className="post"
              style={
                {
                  "--fx": `${l.from[0]}px`,
                  "--fy": `${l.from[1]}px`,
                  "--delay": `${l.delay}s`,
                  "--dur": "5s",
                } as CSSProperties
              }
            >
              <rect width={44} height={30} fill={wraps[i % wraps.length]} stroke="var(--on-wrap)" strokeWidth="2" />
              <path d="M 0 0 L 22 17 L 44 0" fill="none" stroke="var(--on-wrap)" strokeWidth="2" />
            </g>
          </g>
        );
      })}
    </svg>
  );
}
