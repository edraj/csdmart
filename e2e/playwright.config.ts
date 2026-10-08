import { defineConfig, devices } from "@playwright/test";

// The server is started outside Playwright (e2e/run-server.sh) so the same
// script serves local runs and CI; E2E_BASE_URL points at it.
const baseURL = process.env.E2E_BASE_URL ?? "http://127.0.0.1:5399";

export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : [["list"]],
  timeout: 30_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    locale: "en-US",
  },
  projects: [
    // Signs in once per SPA and saves the storage state the other projects use.
    { name: "setup", testMatch: /auth\.setup\.ts/ },
    { name: "chromium", use: { ...devices["Desktop Chrome"] }, dependencies: ["setup"] },
    // The phone-width project exists because the extension-driven walks could
    // never resize the window; this is where narrow layouts get exercised.
    { name: "phone", use: { ...devices["Pixel 7"] }, grep: /@phone/, dependencies: ["setup"] },
  ],
});
