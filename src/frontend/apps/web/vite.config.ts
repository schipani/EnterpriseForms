import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Same-origin /api during development is proxied to the business API (CLAUDE-DESIGN.MD §10).
// API_TARGET selects the compose profile: 5080 = compose.dev.yaml, 5180 = compose.security-test.yaml (used by e2e).
const apiTarget = process.env["API_TARGET"] ?? "http://localhost:5080";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": { target: apiTarget, changeOrigin: false },
      "/health": { target: apiTarget, changeOrigin: false },
    },
  },
  build: {
    sourcemap: false,
    target: "es2022",
  },
});
