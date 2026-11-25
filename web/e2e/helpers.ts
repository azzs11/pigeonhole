import { readFileSync } from "node:fs";
import path from "node:path";
import type { Page } from "@playwright/test";

export const apiKey = () => readFileSync(path.join(import.meta.dirname, ".auth", "key"), "utf8").trim();

export async function signIn(page: Page) {
  await page.addInitScript((key) => sessionStorage.setItem("pigeonhole.apiKey", key), apiKey());
}
