import { test as base, expect, type Page } from "@playwright/test";

export const ADMIN = process.env.E2E_ADMIN ?? "dmart";
export const PASSWORD = process.env.E2E_PASSWORD ?? "dmart";
export const CATALOG_STATE = ".auth/catalog.json";
export const CXB_STATE = ".auth/cxb.json";

// Console lines the pages are allowed to emit: the seed ships without the
// optional report workflow / web config entries, and the pages that look for
// them log the 404 and carry on. Anything else — above all an uncaught
// exception, which is how every regression caught in the manual walks showed
// up — fails the test.
const TOLERATED = [
  /Error retrieving item (report_workflow|web_config)/,
  /\[WebSocket\]/,
  // The browser's own line for the boot session probe (GET /user/profile)
  // answering 401 to an anonymous visitor; not an application error.
  /Failed to load resource: the server responded with a status of 401/,
];

type Fixtures = { page: Page };

export const test = base.extend<Fixtures>({
  page: async ({ page }, use, testInfo) => {
    // The shipped catalog config defaults first-time visitors to Arabic; the
    // assertions below read English chrome, and the Arabic switch is its own
    // test. The stored preference is what the locale bootstrap reads first.
    await page.addInitScript(() => {
      try {
        if (!localStorage.getItem("preferred_locale")) localStorage.setItem("preferred_locale", JSON.stringify("en"));
      } catch {
        /* storage unavailable: the test then runs in the default locale */
      }
    });
    const problems: string[] = [];
    page.on("pageerror", (err) => problems.push(`pageerror: ${err.message}`));
    page.on("console", (msg) => {
      if (msg.type() !== "error") return;
      const text = msg.text();
      if (TOLERATED.some((rx) => rx.test(text))) return;
      problems.push(`console.error: ${text}`);
    });
    await use(page);
    if (problems.length) {
      testInfo.annotations.push({ type: "console", description: problems.join("\n") });
    }
    expect(problems, "no uncaught exceptions or console errors").toEqual([]);
  },
});

export { expect };

// ---- catalog ----------------------------------------------------------------

export async function catalogChooseMenuItem(page: Page, buttonName: RegExp, itemName: RegExp) {
  await page.getByRole("button", { name: buttonName }).click();
  await page.getByRole("menuitemradio", { name: itemName }).click();
}
