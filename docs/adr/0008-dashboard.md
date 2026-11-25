# 0008. Dashboard and sandbox

- Status: accepted
- Date: 2026-11-25

## Decision

- **Stack:** React 19, TypeScript 5.9, Vite 8, TanStack Query, Tailwind 4, React Router.
  TypeScript stays on 5.9 because `openapi-typescript` declares a TypeScript 5 peer dependency.
  The shadcn/ui primitives (button, card, input, dialog) are **vendored by hand** in the shadcn
  pattern (Radix + `cva` + `tailwind-merge`) rather than through the shadcn CLI. Only four are
  needed, and the CLI's interactive init doesn't fit a scripted setup.
- **Typed client:** `web/openapi/relay.json` is exported from the running API, and
  `openapi-typescript` generates `src/api/schema.d.ts`. Requests go through `openapi-fetch`.
  Endpoints that return `IResult` declare their response types (`.Produces<T>()`) so the client
  stays typed.
- **No CORS:** the Vite dev and preview servers proxy `/v1`, `/docs` and `/openapi` to the API.
  The dashboard holds an API key in `sessionStorage` (per tab) and sends it as a Bearer token.
- **Data for the floor plan:** `GET /v1/stats` (per tenant) and `GET /v1/providers`. Workers write
  provider health every 5 s from Polly's live `CircuitBreakerStateProvider` plus 5-minute
  attempt and failure counts.
- **Sandbox** (`Relay:Sandbox:Enabled`, on only in Development): fault switches stored in one
  `sandbox_switches` row and applied by an `IProviderInterceptor` around every provider call
  (switched off, slow, crash after accept). Also a test-batch endpoint, a simulated hard bounce
  played through the real `DeliveryEventProcessor`, and a promises check. "Nothing sent twice"
  is verified against the Message-IDs that actually reached Mailpit.
- **Design:** the sorting-office floor plan is SVG with a screen-reader text twin. Status
  colours (green, amber, red, plus neutral blue) are always paired with a word, and stamps are
  the only decoration. Motion can be switched off, and `prefers-reduced-motion` is respected.
  Light and dark themes are both checked by axe.

## Not done (vs. the plan)

- The Chaos run page shows the last MailChaos results plus the live sandbox. It does not run
  MailChaos live in the browser.
- Providers can't be reordered by drag; failover order stays in configuration.
- Settings shows the key and quota but doesn't create keys or edit the Guard policy. That's still CLI and API.
- No webhook-replay switch: sandbox mail goes to Mailpit, which sends no webhooks.
