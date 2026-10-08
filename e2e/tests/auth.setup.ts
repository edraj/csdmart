import { test as setup, expect } from "@playwright/test";
import { ADMIN, PASSWORD, CATALOG_STATE, CXB_STATE } from "./support";

// One sign-in per SPA per run, saved as storage state (the HttpOnly session
// cookie plus what each app keeps in localStorage). Tests reuse it instead of
// logging in themselves: the server rate-limits the login endpoint per IP,
// and a parallel suite that signs in per test trips that limit.

setup("catalog: sign in as admin", async ({ page }) => {
  await page.goto("/cat/login");
  await page.locator("#identifier").fill(ADMIN);
  await page.locator("#password").fill(PASSWORD);
  await page.locator("#password").press("Enter");
  await page.waitForURL(/\/cat\/dashboard/);
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  await page.context().storageState({ path: CATALOG_STATE });
});

setup("cxb: sign in as admin", async ({ page }) => {
  await page.goto("/cxb/management");
  await page.locator("#username").fill(ADMIN);
  await page.locator("#password").fill(PASSWORD);
  await page.locator("#password").press("Enter");
  await expect(page.getByRole("navigation", { name: /primary navigation/i })).toBeVisible({ timeout: 20_000 });
  await page.context().storageState({ path: CXB_STATE });
});
