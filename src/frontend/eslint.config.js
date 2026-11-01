// Frontend architecture fitness functions (CLAUDE.md §4U.5 item 2). A violation fails CI.
import globals from "globals";
import tseslint from "typescript-eslint";

const transportMessage =
  "All server I/O goes through the app-shell request coordinator (ADR-PB-007). No raw fetch/axios in features or UI packages.";

export default tseslint.config(
  {
    ignores: ["**/dist/**", "**/node_modules/**", "packages/api-client/src/generated/**", "**/*.config.*"],
  },
  ...tseslint.configs.recommended,
  {
    languageOptions: {
      globals: { ...globals.browser, ...globals.es2023 },
    },
  },
  {
    // Features: public-entrypoint imports only, no standalone transport, no direct package internals.
    files: ["apps/web/src/features/**/*.{ts,tsx}"],
    rules: {
      "no-restricted-globals": ["error", { name: "fetch", message: transportMessage }, { name: "XMLHttpRequest", message: transportMessage }],
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            { group: ["axios", "ky", "got", "node-fetch", "undici"], message: transportMessage },
            { group: ["**/features/*/internal/**", "**/features/*/internal"], message: "Cross-feature imports use the owning feature's index.ts public exports only (ADR-ARCH-001)." },
            { group: ["**/features/*/{components,hooks,api,schemas,types,pages}/**"], message: "Import another feature's public index.ts, never its implementation folders." },
            { group: ["@enterpriseforms/*/src/*"], message: "Import package public entrypoints, not src paths." },
          ],
        },
      ],
    },
  },
  {
    // Shared UI/design packages have zero business or API dependencies (ADR-ARCH-001 §4U.4).
    files: ["packages/ui/**/*.{ts,tsx}", "packages/design-tokens/**/*.{ts,tsx}", "packages/form-renderer/**/*.{ts,tsx}", "packages/form-designer/**/*.{ts,tsx}"],
    rules: {
      "no-restricted-globals": ["error", { name: "fetch", message: transportMessage }],
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            { group: ["@enterpriseforms/api-client", "**/features/**", "**/apps/**"], message: "UI packages must not depend on business features or the API client." },
          ],
        },
      ],
    },
  },
  {
    // Platform Console (C8 M2 Stage 4): a separate website. It may use the shared UI and design tokens only; no business
    // packages, no request coordinator, and network access exists in exactly one module (src/api/client.ts).
    files: ["apps/platform-console/src/**/*.{ts,tsx}"],
    rules: {
      "no-restricted-globals": ["error", { name: "fetch", message: "Only apps/platform-console/src/api/client.ts talks to the network." }, { name: "XMLHttpRequest", message: transportMessage }, { name: "localStorage", message: "The console stores nothing in the browser (no tokens, CSRF secrets or evidence)." }, { name: "sessionStorage", message: "The console stores nothing in the browser (no tokens, CSRF secrets or evidence)." }],
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            { group: ["axios", "ky", "got", "node-fetch", "undici"], message: transportMessage },
            { group: ["@enterpriseforms/api-client", "@enterpriseforms/app-runtime", "@enterpriseforms/work-queue", "@enterpriseforms/upload-manager", "@enterpriseforms/form-designer", "@enterpriseforms/form-renderer", "**/apps/web/**"], message: "The Platform Console is a separate site: shared UI and design tokens only, never business packages." },
          ],
        },
      ],
    },
  },
  {
    files: ["apps/platform-console/src/api/client.ts"],
    rules: { "no-restricted-globals": ["error", { name: "localStorage", message: "No browser storage." }, { name: "sessionStorage", message: "No browser storage." }] },
  },
  {
    // App shell composes feature public exports only.
    files: ["apps/web/src/app/**/*.{ts,tsx}"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            { group: ["**/features/*/{components,hooks,api,schemas,types,internal}/**"], message: "The app shell imports feature public entrypoints only." },
          ],
        },
      ],
    },
  },
);
