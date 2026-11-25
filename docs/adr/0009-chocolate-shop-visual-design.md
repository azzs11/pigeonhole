# 0009. Visual design: chocolate-shop look with motion

- Status: accepted (refines 0008)
- Date: 2026-11-26

## Context

The first dashboard (ADR 0008) used a navy-and-paper postal palette with rounded cards. It was
clean but static and looked like a stock admin template. The brief was a bolder, warmer
retail-packaging feel: cream paper, ink-black type, chunky condensed headings, flat colour blocks
and visible motion.

## Decision

- **Tokens** (`web/src/index.css`): cream `--paper`, ink `--ink`, cocoa `--accent`, green
  `--hover`, and six decorative **wrapper** colours (`--wrap-*`). Square corners everywhere
  (Tailwind radius tokens set to 0), 2 px ink borders, a hard offset shadow on hover.
- **Type:** Barlow Condensed 800 for headings and signage, DM Sans for body text, IBM Plex Mono
  for IDs and logs (Google Fonts, OFL). The brand that inspired the look uses proprietary fonts,
  so we copy the feel, not the assets.
- **Colour rules:** status colours (good, warn, bad, neutral) still mean status only and come
  with a word. Wrapper colours are decoration only and never signal state. Text on a wrapper
  uses `--on-wrap` (ink in both themes), so contrast holds in dark mode.
- **Motion:** a live ticker of real numbers, sections that slide in on load, count-up numbers,
  tiles that lift on hover, buttons that press, stamps that land in the timeline, and on the
  landing page an animated wall of pigeonholes. All of it stops with the Motion toggle
  (`html[data-motion=off]`) and with `prefers-reduced-motion`.
- **Copy:** short and plain, with no slogan grids. The landing page leads with the demo button.

## Consequences

- The axe checks run after entrance animations finish (text halfway through a fade fails
  contrast). Looping animations are ignored.
- The light-mode green was darkened to `#22703d` so large green text passes contrast.
