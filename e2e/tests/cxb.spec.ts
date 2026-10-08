import { test, expect, CXB_STATE } from "./support";

test.describe("cxb", () => {
  test("landing page renders with its header controls", async ({ page }) => {
    await page.goto("/cxb/");
    await expect(page.getByRole("heading", { name: /What is DMART\?/ })).toBeVisible();
    await expect(page.getByRole("button", { name: "Language" })).toBeVisible();
  });

  test("anonymous /management shows the login form, not a blank shell", async ({ page }) => {
    await page.goto("/cxb/management");
    await expect(page.locator("#username")).toBeVisible();
    await expect(page.locator("#password")).toBeVisible();
    // The 24-character cap on the password field is gone.
    await expect(page.locator("#password")).not.toHaveAttribute("maxlength", /.+/);
  });

  test.describe("signed in", () => {
    test.use({ storageState: CXB_STATE });

    test("spaces page lists the seeded spaces as links", async ({ page }) => {
      await page.goto("/cxb/management/content");
      await expect(page.getByRole("heading", { level: 1, name: "Spaces" })).toBeVisible();
      await expect(page.getByRole("link", { name: /Website/ }).first()).toBeVisible();
      await expect(page.getByRole("link", { name: /Management/ }).first()).toBeVisible();
    });

    test("a space opens as a table with sentence-case headers, rows and a pager", async ({ page }) => {
      await page.goto("/cxb/management/content");
      await page.getByRole("link", { name: /Website/ }).first().click();
      await expect(page).toHaveURL(/\/cxb\/management\/content\/website/);
      const table = page.getByRole("table");
      await expect(table).toBeVisible();
      await expect(table.getByRole("columnheader", { name: /Updated at/ })).toBeVisible();
      await expect(table.getByRole("row")).not.toHaveCount(1);
      await expect(page.getByText(/Showing \d+ to \d+ of \d+/)).toBeVisible();
    });

    test("opening an entry shows its tabs and does not prompt to leave", async ({ page }) => {
      await page.goto("/cxb/management/content/website/pages");
      await page.getByRole("link", { name: /^why$/ }).first().click();
      await expect(page.getByRole("tab", { name: /Entry/ })).toBeVisible();
      await page.getByRole("tab", { name: /Form/ }).click();
      // A page that nobody edited must navigate away without a beforeunload prompt.
      page.on("dialog", (d) => {
        throw new Error(`unexpected dialog: ${d.message()}`);
      });
      await page.goto("/cxb/management/content");
      await expect(page.getByRole("heading", { level: 1, name: "Spaces" })).toBeVisible();
    });

    test("tools pages render", async ({ page }) => {
      await page.goto("/cxb/management/tools");
      await expect(page.getByRole("heading", { level: 1, name: "Tools" })).toBeVisible();
      await page.getByRole("link", { name: /Trash/ }).first().click();
      await expect(page.getByRole("heading", { level: 1, name: /Trash/ })).toBeVisible();
    });

    test("switching to Arabic flips direction and translates the chrome", async ({ page }) => {
      await page.goto("/cxb/management/content");
      await page.getByRole("button", { name: "Language" }).click();
      await page.getByText("العربية", { exact: true }).click();
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
      await expect(page.locator("html")).toHaveAttribute("lang", "ar");
      await expect(page.getByRole("heading", { level: 1 })).not.toHaveText("Spaces");
      // Back to English for the other tests (per-test contexts, but keep the
      // stored preference tidy).
      await page.getByRole("button", { name: /اللغة/ }).click();
      await page.getByText("English", { exact: true }).click();
      await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    });

    test("spaces page at phone width has no horizontal overflow @phone", async ({ page }) => {
      await page.goto("/cxb/management/content");
      await expect(page.getByRole("heading", { level: 1, name: "Spaces" })).toBeVisible();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, "horizontal overflow in px").toBeLessThanOrEqual(0);
    });
  });
});
