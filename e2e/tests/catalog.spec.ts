import { test, expect, catalogChooseMenuItem, CATALOG_STATE, ADMIN, PASSWORD } from "./support";

// The bundled sample spaces grant nothing to anonymous visitors, so even the
// public browse pages are exercised with a session; the sign-out test at the
// end covers the anonymous landing.
test.describe("catalog (public pages)", () => {
  test.use({ storageState: CATALOG_STATE });

  test("home lists the seeded spaces and the counters", async ({ page }) => {
    await page.goto("/cat/");
    await expect(page).toHaveTitle(/Explore spaces/);
    await expect(page.getByRole("heading", { level: 1, name: "Explore spaces" })).toBeVisible();
    await expect(page.getByRole("link", { name: /Management/ })).toBeVisible();
    await expect(page.getByRole("link", { name: /Website/ })).toBeVisible();
  });

  test("a post opens from the space with breadcrumb, rendered body and copy link", async ({ page }) => {
    await page.goto("/cat/catalogs/website/pages");
    // Fourteen pages sorted by name, ten per page: find the post through the
    // listing's own search rather than paging.
    const search = page.getByPlaceholder(/Search by name/);
    await search.fill("Why DMART");
    await search.press("Enter");
    await page.getByRole("link", { name: /Why DMART\?/ }).first().click();
    await expect(page).toHaveURL(/\/cat\/catalogs\/website\/pages\/why\/content/);
    await expect(page).toHaveTitle(/Why DMART\?/);
    await expect(page.getByRole("heading", { level: 1, name: "Why DMART?" })).toBeVisible();
    // Breadcrumb trail and the rendered markdown (a heading from the body).
    await expect(page.getByRole("navigation", { name: /breadcrumb/i })).toContainText("pages");
    await expect(page.getByRole("heading", { name: "Data First Philosophy" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Copy link" })).toBeVisible();
  });

  test("share links resolve (no 404 on a copied post URL)", async ({ page }) => {
    const resp = await page.goto("/cat/catalogs/website/pages/why/content");
    expect(resp?.status()).toBe(200);
    await expect(page.getByRole("heading", { level: 1, name: "Why DMART?" })).toBeVisible();
  });

  test("language menu switches to Arabic: lang, dir and translated chrome", async ({ page }) => {
    await page.goto("/cat/");
    await catalogChooseMenuItem(page, /Change language/, /العربية/);
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).not.toHaveText("Explore spaces");
    // Back to English without a reload.
    await catalogChooseMenuItem(page, /تغيير اللغة/, /English/);
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(page.getByRole("heading", { level: 1, name: "Explore spaces" })).toBeVisible();
  });

  test("theme menu: dark sets data-theme and a dark surface", async ({ page }) => {
    await page.goto("/cat/");
    await catalogChooseMenuItem(page, /Change theme/, /^Dark$/);
    await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
    const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    const [r, g, b] = bg.match(/\d+/g)!.map(Number);
    expect(r + g + b, `body background ${bg} should be dark`).toBeLessThan(200);
    await catalogChooseMenuItem(page, /Change theme/, /^Light$/);
    await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  });

  // ```mermaid fences are drawn, by mermaid bundled into the catalog (the CSP
  // takes scripts from 'self' only), and drawn again when the theme changes.
  test("a post's mermaid diagrams are drawn and follow the theme", async ({ page }) => {
    await page.goto("/cat/catalogs/website/pages/features/content");
    const drawn = page.locator("figure.md-diagram[data-rendered] svg");
    await expect(drawn).toHaveCount(3);
    await expect(drawn.first()).toContainText("Space: Project/Business");
    await expect(page.locator(".md-diagram-error")).toHaveCount(0);
    await catalogChooseMenuItem(page, /Change theme/, /^Dark$/);
    await expect(page.locator('figure.md-diagram[data-rendered="dark"] svg')).toHaveCount(3);
    await catalogChooseMenuItem(page, /Change theme/, /^Light$/);
    await expect(page.locator('figure.md-diagram[data-rendered="light"] svg')).toHaveCount(3);
  });

  test("unknown route shows the 404 page, not a blank screen", async ({ page }) => {
    await page.goto("/cat/no/such/page");
    await expect(page.getByText(/could not be found/)).toBeVisible();
    await expect(page.getByRole("link", { name: /home/i }).first()).toBeVisible();
  });

  test("home at phone width has no horizontal overflow and a menu button @phone", async ({ page }) => {
    await page.goto("/cat/");
    await expect(page.getByRole("heading", { level: 1, name: "Explore spaces" })).toBeVisible();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow, "horizontal overflow in px").toBeLessThanOrEqual(0);
    await expect(page.getByRole("button", { name: /^Menu$/ })).toBeVisible();
  });
});

test.describe("catalog (dashboard)", () => {
  test.use({ storageState: CATALOG_STATE });

  test("admin dashboard lists the spaces", async ({ page }) => {
    await page.goto("/cat/dashboard/admin");
    await expect(page.getByRole("heading", { level: 1, name: "Admin Dashboard" })).toBeVisible();
    await expect(page.getByRole("row", { name: /Management/ })).toBeVisible();
    await expect(page.getByRole("row", { name: /Website/ })).toBeVisible();
  });

  test("user management loads the seeded users after a cold load", async ({ page }) => {
    // Regression: the SDK's default scope used to be `public` after a reload,
    // so this page could never list users.
    await page.goto("/cat/dashboard/admin/users");
    await expect(page.getByRole("heading", { level: 1, name: "User Management" })).toBeVisible();
    await expect(page.getByRole("row", { name: /dmart/ })).toBeVisible();
    await expect(page.getByText(/Failed to load users/)).toHaveCount(0);
  });

  test("a user's mailbox, aliases and services are edited in User Management", async ({ page }) => {
    // Cookie-authenticated API calls need the CSRF signal a browser fetch sends.
    const api = (path: string, data: object) =>
      page.request.post(path, { headers: { "X-Requested-With": "XMLHttpRequest" }, data });
    const sn = `catmail${Date.now() % 100000}`;
    const created = await api("/managed/request", {
      space_name: "management", request_type: "create",
      records: [{ resource_type: "user", subpath: "users", shortname: sn, attributes: { is_active: true } }],
    });
    expect(created.ok()).toBeTruthy();
    try {
      await page.goto("/cat/dashboard/admin/users");
      const search = page.getByPlaceholder("Search users...");
      await search.fill(sn);
      await search.press("Enter");
      await page.getByRole("button", { name: `Edit user ${sn}` }).click();
      await page.getByRole("button", { name: "Mail and services" }).click();
      await page.getByLabel("Hosted mailbox").fill(`${sn}@Example.ORG`);
      await page.getByLabel("Mail aliases").fill(`help-${sn}@example.org\n\npostmaster-${sn}@Example.org`);
      await page.getByLabel("Services").fill("mail, Matrix");
      await page.getByRole("button", { name: "Update user" }).click();

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

      // Reopened, the section starts open, since the user now has them.
      await page.getByRole("button", { name: `Edit user ${sn}` }).click();
      await expect(page.getByLabel("Hosted mailbox")).toHaveValue(`${sn}@example.org`);
      await expect(page.getByLabel("Services")).toHaveValue("mail, matrix");
    } finally {
      await api("/managed/request", {
        space_name: "management", request_type: "delete",
        records: [{ resource_type: "user", subpath: "users", shortname: sn, attributes: {} }],
      });
    }
  });

  test("the admin space page filters by type without raw keys", async ({ page }) => {
    await page.goto("/cat/dashboard/admin/management");
    await expect(page.getByRole("heading", { level: 1, name: "Management" })).toBeVisible();
    await expect(page.getByText("[object Object]")).toHaveCount(0);
    await expect(page.getByRole("link", { name: /users/ }).first()).toBeVisible();
  });

  test("My Entries lists entries from more than one space", async ({ page }) => {
    // Regression: rows keyed by shortname collided across spaces and the page
    // never left its loading state.
    await page.goto("/cat/entries");
    await expect(page.getByRole("heading", { level: 1, name: "My Entries" })).toBeVisible();
    await expect(page.getByRole("row", { name: /Why DMART\?/ })).toBeVisible();
    await expect(page.getByRole("row", { name: /schema/ }).first()).toBeVisible();
  });

  test("entry view, edit and create pages render", async ({ page }) => {
    await page.goto("/cat/entries/website/pages/why/content");
    await expect(page.getByRole("heading", { level: 1, name: "Why DMART?" })).toBeVisible();
    await expect(page.getByText(/Edit Entry/)).toBeVisible();
    await page.goto("/cat/entries/website/pages/why/content/edit");
    await expect(page.getByText(/Save Changes/)).toBeVisible();
    await page.goto("/cat/entries/create");
    await expect(page.getByRole("heading", { level: 1, name: /Create/ })).toBeVisible();
    await expect(page.getByText("[object Object]")).toHaveCount(0);
  });

  test("the entry view draws mermaid diagrams", async ({ page }) => {
    await page.goto("/cat/entries/website/pages/features/content");
    await expect(page.locator("figure.md-diagram[data-rendered] svg")).toHaveCount(3);
    await expect(page.locator(".md-diagram-error")).toHaveCount(0);
  });

  test("notifications, messaging, polls and surveys render their empty states", async ({ page }) => {
    for (const [path, heading] of [
      ["/cat/notifications", /Notifications/],
      ["/cat/polls", /Polls/],
      ["/cat/surveys", /Surveys/],
      ["/cat/messaging", /Messages/],
    ] as const) {
      await page.goto(path);
      await expect(page.getByRole("heading", { level: 1, name: heading })).toBeVisible();
    }
  });

});

test.describe("catalog (sign out)", () => {
  // Signing out revokes the session on the server, so this test must not use
  // the shared storage state: it signs in on its own and ends anonymous.
  test.use({ storageState: { cookies: [], origins: [] } });

  test("signing out ends the session and lands on the login page", async ({ page }) => {
    await page.goto("/cat/login");
    await page.locator("#identifier").fill(ADMIN);
    await page.locator("#password").fill(PASSWORD);
    await page.locator("#password").press("Enter");
    await page.waitForURL(/\/cat\/dashboard/);
    await page.getByRole("button", { name: /^Menu$/ }).click();
    await page.getByRole("menuitem", { name: /Sign Out/ }).click();
    await expect(page).toHaveURL(/\/cat\/login/);
    await expect(page.locator("#identifier")).toBeVisible();
    // The anonymous landing renders rather than a blank page.
    await page.goto("/cat/");
    await expect(page.getByRole("heading", { level: 1, name: "Explore spaces" })).toBeVisible();
  });
});
