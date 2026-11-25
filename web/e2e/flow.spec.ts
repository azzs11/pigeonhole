import { expect, test } from "@playwright/test";
import { apiKey } from "./helpers";

test("send, watch the timeline, bounce into a suppression, approve a held message", async ({ page, request }) => {
  const id = Date.now();
  const to = `reader-${id}@example.test`;
  const subject = `Hello from e2e ${id}`;

  // Sign in through the real form once.
  await page.goto("/");
  await page.getByLabel(/API key/).fill(apiKey());
  await page.getByRole("button", { name: "Open", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Sorting office" })).toBeVisible();

  // Send, then watch the timeline reach Sent (the worker delivers to Mailpit).
  await page.getByRole("link", { name: "Messages" }).click();
  await page.getByLabel("To").fill(to);
  await page.getByLabel("Subject").fill(subject);
  await page.getByLabel("Text").fill("Watching this one travel.");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await page.getByRole("button", { name: subject }).click();
  const panel = page.getByRole("dialog");
  await expect(panel.locator('[data-status="Sent"]')).toBeVisible();
  await expect(panel.getByText("The provider accepted it")).toBeVisible();

  // A bounce becomes a suppression.
  await panel.getByRole("button", { name: "Simulate a bounce" }).click();
  await expect(panel.locator('[data-status="Bounced"]')).toBeVisible();
  await page.keyboard.press("Escape");
  await page.getByRole("link", { name: "Suppressions" }).click();
  await expect(page.getByText(to)).toBeVisible();

  // An agent mails someone the run did not name: the Guard holds it; a human approves it.
  const operator = { Authorization: `Bearer ${apiKey()}` };
  const run = await (
    await request.post("/v1/agent-runs", {
      headers: operator,
      data: { trustedRecipients: ["maya@example.test"] },
    })
  ).json();
  const heldSubject = `Held for review ${id}`;
  const held = await request.post("/v1/agent/messages", {
    headers: { Authorization: `Bearer ${run.token}` },
    data: {
      to: [`vendor-${id}@outside.test`],
      subject: heldSubject,
      text: "Quote attached.",
    },
  });
  expect((await held.json()).status).toBe("Held");

  await page.getByRole("link", { name: /Approvals/ }).click();
  const card = page.getByRole("article").filter({ hasText: heldSubject });
  await expect(card.getByText(/was not named on this run/)).toBeVisible();
  await card.getByRole("button", { name: /Approve/ }).click();
  await expect(card).toHaveCount(0);

  await page.getByRole("link", { name: "Messages" }).click();
  const row = page.getByRole("row").filter({ hasText: heldSubject });
  await expect(row.locator('[data-status="Sent"]')).toBeVisible();
});
