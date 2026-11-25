// Takes the README screenshots from a running sandbox stack (`docker compose up`).
// Usage (from web/): node scripts/screenshots.mjs [baseUrl]   → writes ../docs/images/*.png
// It uses a fresh sandbox demo key, sends a small batch and one agent message the Guard holds,
// so every page has something real on it. Nothing leaves the test inbox.
import { mkdirSync } from "node:fs";
import path from "node:path";
import { chromium } from "@playwright/test";

const base = process.argv[2] ?? "http://localhost:5173";
const out = path.resolve(import.meta.dirname, "../../docs/images");
mkdirSync(out, { recursive: true });

const post = async (url, body, key) => {
  const r = await fetch(base + url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(key ? { Authorization: `Bearer ${key}` } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) throw new Error(`${url} answered ${r.status}`);
  return r.json();
};

const { key } = await post("/v1/sandbox/demo-key");
await post("/v1/sandbox/batch", { count: 120 }, key);
const run = await post("/v1/agent-runs", { trustedRecipients: ["maya@example.test"] }, key);
await post(
  "/v1/agent/messages",
  {
    to: ["vendor@outside.test"],
    subject: "Quote for 40 desks",
    text: "Quote attached.",
  },
  run.token,
);
await new Promise((r) => setTimeout(r, 4000));

const browser = await chromium.launch();
const shoot = async (name, route, { scheme = "light", signedIn = true, full = false } = {}) => {
  const ctx = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    deviceScaleFactor: 2,
    colorScheme: scheme,
  });
  if (signedIn) await ctx.addInitScript((k) => sessionStorage.setItem("pigeonhole.apiKey", k), key);
  await ctx.addInitScript(() => localStorage.setItem("pigeonhole.motion", "off"));
  const page = await ctx.newPage();
  await page.goto(base + route);
  await page.waitForLoadState("networkidle");
  await page.waitForTimeout(800);
  await page.screenshot({
    path: path.join(out, `${name}.png`),
    fullPage: full,
  });
  await ctx.close();
  console.log(`docs/images/${name}.png`);
};

await shoot("landing", "/", { signedIn: false });
await shoot("sorting-office", "/", { full: true });
await shoot("sorting-office-dark", "/", { scheme: "dark" });
await shoot("approvals", "/approvals");
await shoot("messages", "/messages");
await browser.close();
