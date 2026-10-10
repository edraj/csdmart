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

    // The other tests open pages by URL; this one clicks through the header,
    // which is how a person gets around. A `$goto` first read inside a click
    // handler found no router context, so the tabs and the account menu's
    // Profile did nothing (a console error the page fixture fails on).
    test("the header's tabs and the account menu's Profile navigate", async ({ page }) => {
      await page.goto("/cxb/management/content");
      await page.getByRole("link", { name: "Tools" }).first().click();
      await expect(page).toHaveURL(/\/cxb\/management\/tools/);
      await page.locator("#management-user-menu").click();
      await page.getByText("Profile", { exact: true }).click();
      await expect(page).toHaveURL(/\/cxb\/management\/profile/);
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

    test("a user's mailbox, aliases and services are edited in the user form", async ({ page }) => {
      // Cookie-authenticated API calls need the CSRF signal a browser fetch sends.
      const api = (path: string, data: object) =>
        page.request.post(path, { headers: { "X-Requested-With": "XMLHttpRequest" }, data });
      const sn = `mailuser${Date.now() % 100000}`;
      const created = await api("/managed/request", {
        space_name: "management", request_type: "create",
        records: [{ resource_type: "user", subpath: "users", shortname: sn, attributes: { is_active: true } }],
      });
      expect(created.ok()).toBeTruthy();

      await page.goto(`/cxb/management/content/management/users/${sn}/user`);
      await page.getByRole("tab", { name: /Form/ }).click();
      await page.getByRole("button", { name: "Mail and services" }).click();
      await page.getByLabel("Hosted mailbox").fill(`${sn}@Example.ORG`);
      await page.getByLabel("Mail aliases").fill(`help-${sn}@example.org\n\npostmaster-${sn}@Example.org`);
      await page.getByLabel("Services").fill("mail, Matrix");
      await page.getByRole("button", { name: "Save" }).click();

      // Stored as the server normalizes them: folded, blank lines dropped.
      await expect(async () => {
        const res = await api("/managed/query", {
          space_name: "management", type: "search", subpath: "users", filter_types: ["user"], filter_shortnames: [sn],
        });
        const attrs = (await res.json()).records?.[0]?.attributes ?? {};
        expect(attrs.mailbox).toBe(`${sn}@example.org`);
        expect(attrs.mail_aliases).toEqual([`help-${sn}@example.org`, `postmaster-${sn}@example.org`]);
        expect(attrs.services).toEqual(["mail", "matrix"]);
      }).toPass({ timeout: 10_000 });

      // The form shows them again after a reload.
      await page.reload();
      await page.getByRole("tab", { name: /Form/ }).click();
      await page.getByRole("button", { name: "Mail and services" }).click();
      await expect(page.getByLabel("Hosted mailbox")).toHaveValue(`${sn}@example.org`);
      await expect(page.getByLabel("Services")).toHaveValue("mail, matrix");

      await api("/managed/request", {
        space_name: "management", request_type: "delete",
        records: [{ resource_type: "user", subpath: "users", shortname: sn, attributes: {} }],
      });
    });

    test("an edit made before the editor settles still enables Save", async ({ page }) => {
      // The editor takes its baseline for the unsaved-changes check a moment
      // after mounting. An edit typed before then used to become part of the
      // baseline, leaving Save disabled; CI's runner hit exactly that. The
      // page clock is held so the edit always lands first.
      await page.clock.install();
      await page.goto("/cxb/management/content/management/users/dmart/user");
      await page.getByRole("tab", { name: /Form/ }).click();
      await page.getByLabel("Preferred language").fill("arabic");
      await page.clock.runFor(2000);
      await expect(page.getByRole("button", { name: "Save" })).toBeEnabled();
    });

    test("spaces page at phone width has no horizontal overflow @phone", async ({ page }) => {
      await page.goto("/cxb/management/content");
      await expect(page.getByRole("heading", { level: 1, name: "Spaces" })).toBeVisible();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, "horizontal overflow in px").toBeLessThanOrEqual(0);
    });
  });
});
