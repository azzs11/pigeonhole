// Records the README GIF from a running sandbox stack (`docker compose up`):
// landing → "Try the sandbox" → send a batch and watch it drain → open one message's timeline.
// Usage (from web/): node scripts/record-gif.mjs [baseUrl]   → writes ../docs/images/demo.gif
// Frames are PNG screenshots; ../bench/.venv (Pillow, installed with matplotlib) stitches them.
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { chromium } from "@playwright/test";

const base = process.argv[2] ?? "http://localhost:5173";
const root = path.resolve(import.meta.dirname, "../..");
const dir = mkdtempSync(path.join(os.tmpdir(), "pigeonhole-gif-"));
let n = 0;
const frames = [];

const browser = await chromium.launch();
const page = await (
  await browser.newContext({
    viewport: { width: 1280, height: 800 },
    colorScheme: "light",
  })
).newPage();
const snap = async (ms, count = 1) => {
  for (let i = 0; i < count; i++) {
    const file = path.join(dir, `${String(n++).padStart(4, "0")}.png`);
    await page.screenshot({ path: file });
    frames.push([file, ms]);
    if (count > 1) await page.waitForTimeout(ms);
  }
};

await page.goto(base);
await page.getByRole("button", { name: /Try the sandbox/ }).waitFor();
await snap(2200);
await page.getByRole("button", { name: /Try the sandbox/ }).click();
await page.getByRole("heading", { level: 1, name: "Sorting office" }).waitFor();
await page.waitForLoadState("networkidle");
await snap(1500);
await page.getByRole("button", { name: /Send a test batch/ }).click();
await snap(350, 30); // counters climb, envelopes travel, Intake drains into Delivered
await page.getByRole("link", { name: "Messages" }).click();
await page.getByRole("heading", { level: 1, name: "Messages" }).waitFor();
await page.waitForLoadState("networkidle");
await snap(1500);
await page.locator("tbody button").first().click();
await page.getByRole("heading", { name: "Message timeline" }).waitFor();
await page.waitForTimeout(600);
await snap(3500);
await browser.close();

const out = path.join(root, "docs/images/demo.gif");
const py = `
import sys
from PIL import Image
args = sys.argv[1:]
out, pairs = args[0], args[1:]
frames, durations = [], []
for i in range(0, len(pairs), 2):
    im = Image.open(pairs[i]).convert("RGB")
    im = im.resize((960, int(im.height * 960 / im.width)), Image.LANCZOS)
    frames.append(im.quantize(colors=128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE))
    durations.append(int(pairs[i + 1]))
frames[0].save(out, save_all=True, append_images=frames[1:], duration=durations, loop=0, optimize=True)
`;
execFileSync(path.join(root, "bench/.venv/bin/python"), ["-I", "-c", py, out, ...frames.flat().map(String)], { stdio: "inherit" });
rmSync(dir, { recursive: true, force: true });
console.log("docs/images/demo.gif");
