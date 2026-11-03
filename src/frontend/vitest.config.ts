import { availableParallelism } from "node:os";
import { defineConfig } from "vitest/config";

/**
 * Worker count (C-106). Vitest's default is one worker per logical core minus one. Every worker is a separate jsdom environment that
 * imports React, Radix and TanStack Query, so on a many-core developer machine (16 cores = 15 workers) the import and transform phases
 * and the heavier user-event suites (PeopleDirectoryPage, OrganizationOnboarding, App) compete for CPU, and any test whose own work
 * lands near the 5 s default can time out. That is contention, not a test defect: each of those files passes alone and the whole suite
 * passes with fewer workers. The count is therefore bounded to a quarter of the cores, between 2 and 4; assertions and timeouts are
 * unchanged. Override with VITEST_MAX_WORKERS (for example in a constrained CI runner).
 */
const configured = Number(process.env["VITEST_MAX_WORKERS"]);
const maxWorkers = Number.isInteger(configured) && configured > 0 ? configured : Math.max(2, Math.min(4, Math.floor(availableParallelism() / 4)));

export default defineConfig({
  test: {
    include: ["packages/**/src/**/*.test.{ts,tsx}", "apps/**/src/**/*.test.{ts,tsx}", "e2e/**/*.unit.test.ts"],
    environment: "jsdom",
    setupFiles: ["./vitest.setup.ts"],
    passWithNoTests: false,
    maxWorkers,
  },
});
