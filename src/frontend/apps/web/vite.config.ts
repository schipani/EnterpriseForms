import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Same-origin /api during development is proxied to the business API (CLAUDE-DESIGN.MD §10).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": { target: "http://localhost:5080", changeOrigin: false },
      "/health": { target: "http://localhost:5080", changeOrigin: false },
    },
  },
  build: {
    sourcemap: false,
    target: "es2022",
  },
});
