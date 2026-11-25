/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";

// The dashboard talks to the API through this proxy in dev and preview, so no CORS is needed.
const api = process.env.RELAY_API ?? "http://localhost:5238";
const proxy = Object.fromEntries(["/v1", "/docs", "/openapi", "/health"].map((p) => [p, { target: api, changeOrigin: true }]));

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { "@": path.resolve(import.meta.dirname, "src") } },
  server: { port: 5173, proxy, fs: { allow: [".."] } },
  preview: { port: 4173, proxy },
  test: {
    environment: "jsdom",
    setupFiles: ["src/test/setup.ts"],
    include: ["src/**/*.test.tsx"],
    css: false,
  },
});
