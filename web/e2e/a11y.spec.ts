import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { signIn } from "./helpers";

const pages = [
  ["/", "Sorting office"],
  ["/messages", "Messages"],
  ["/approvals", "Approvals"],
  ["/suppressions", "Suppressions"],
  ["/providers", "Providers"],
  ["/chaos", "Chaos run"],
  ["/settings", "Settings"],
] as const;

async function serious(page: import("@playwright/test").Page) {
  const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  return result.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(" ")).join(", ")})`);
}

test("sign-in page has no serious accessibility violations", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByLabel("API key")).toBeVisible();
  expect(await serious(page)).toEqual([]);
});

for (const scheme of ["light", "dark"] as const) {
  for (const [route, heading] of pages) {
    test(`${heading} (${scheme}) has no serious accessibility violations`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await signIn(page);
      await page.goto(route);
      await expect(page.getByRole("heading", { level: 1, name: heading })).toBeVisible();
      await page.waitForLoadState("networkidle");
      expect(await serious(page)).toEqual([]);
    });
  }
}
