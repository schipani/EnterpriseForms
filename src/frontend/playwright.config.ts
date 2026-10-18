import { defineConfig, devices } from "@playwright/test";

/**
 * Browser end-to-end tests for the Stage B Accused workflow. They run against the Vite dev server proxying to the
 * business API of the running Docker stack (API_TARGET, default: the security-test profile on 5180) and use the
 * seeded development personas. No mocks: every step is a real request through the BFF session.
 */
export default defineConfig({
  testDir: "./e2e",
  timeout: 180_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["html", { open: "never", outputFolder: "../../artifacts/stage-b/playwright-report" }], ["junit", { outputFile: "../../artifacts/stage-b/playwright-junit.xml" }]],
  use: {
    baseURL: process.env["E2E_BASE_URL"] ?? "http://127.0.0.1:5173",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command: "npm run dev -w apps/web -- --host 127.0.0.1",
    url: "http://127.0.0.1:5173",
    reuseExistingServer: true,
    timeout: 120_000,
    env: { API_TARGET: process.env["API_TARGET"] ?? "http://127.0.0.1:5180" },
  },
  projects: [
    { name: "chromium-desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } }, testIgnore: /responsive\.spec\.ts/ },
    { name: "chromium-phone", use: { ...devices["Desktop Chrome"], viewport: { width: 320, height: 720 }, isMobile: false }, testMatch: /responsive\.spec\.ts/ },
  ],
});
