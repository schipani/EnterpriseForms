import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Same-origin /console-api during development is proxied to the Platform Console BFF (the browser's Origin header is forwarded
// unchanged, so the BFF's origin allow-list must name this dev server). CONSOLE_TARGET: 8092 = persistent development console,
// 5192 = the isolated security-test console used by Playwright. The business API is never proxied here.
const target = process.env["CONSOLE_TARGET"] ?? "http://127.0.0.1:8092";
const port = Number(process.env["PLATFORM_CONSOLE_PORT"] ?? "5195");

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port,
    strictPort: true,
    proxy: { "/console-api": { target, changeOrigin: false } },
  },
  build: { sourcemap: false, target: "es2022" },
});
