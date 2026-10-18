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
