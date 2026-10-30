import { defineConfig, devices } from "@playwright/test";
import { assertNotPersistentEndpoint } from "./e2e/isolation";

/**
 * Browser end-to-end tests. They run against a Vite dev server that Playwright itself starts on a dedicated port and that
 * proxies to the isolated security-test API (API_TARGET, default 5180). They create and change data, so (C7 M4 incident,
 * docs/PHASE_C7_STATUS.md §31):
 *  - the dedicated port (E2E_PORT, default 5199) is never shared with a developer's Vite server (5173), and an already
 *    running server there is never reused (`reuseExistingServer: false`: Playwright refuses to start instead);
 *  - persistent-environment endpoints (8091, 5280, 5080) are refused here, before anything starts;
 *  - the global setup proves through the browser's own base URL that the API and its Authority are the isolated Testing
 *    stack and that no persistent organization is served, using GET requests only, before the first spec;
 *  - `signIn` (the first step of every write path) refuses any origin the global setup did not verify.
 */
const port = process.env["E2E_PORT"] ?? "5199";
const serverUrl = `http://127.0.0.1:${port}`;
const baseURL = process.env["E2E_BASE_URL"] ?? serverUrl;
const apiTarget = process.env["API_TARGET"] ?? "http://127.0.0.1:5180";
assertNotPersistentEndpoint("E2E_BASE_URL", baseURL);
assertNotPersistentEndpoint("API_TARGET", apiTarget);
if (port === "5173") {
  throw new Error("Browser tests refused to start: E2E_PORT 5173 is the developer Vite port; use a dedicated port.");
}

export default defineConfig({
  testDir: "./e2e",
  testMatch: /.*\.spec\.ts$/,
  globalSetup: "./e2e/global-setup.ts",
  timeout: 180_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["html", { open: "never", outputFolder: "../../artifacts/stage-b/playwright-report" }], ["junit", { outputFile: "../../artifacts/stage-b/playwright-junit.xml" }]],
  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command: `npm run dev -w apps/web -- --host 127.0.0.1 --port ${port} --strictPort`,
    url: serverUrl,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { API_TARGET: apiTarget },
  },
  projects: [
    { name: "chromium-desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } }, testIgnore: /responsive\.spec\.ts/ },
    { name: "chromium-phone", use: { ...devices["Desktop Chrome"], viewport: { width: 320, height: 720 }, isMobile: false }, testMatch: /responsive\.spec\.ts/ },
  ],
});
