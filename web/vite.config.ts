import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "path";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "./src"),
    },
  },
  server: {
    port: 3000,
    strictPort: false,
    proxy: {
      // Backend (src/Crm.Api) listens on 5080 in development.
      "/api": {
        target: process.env.VITE_API_PROXY_TARGET || "http://localhost:5080",
        changeOrigin: true,
      },
    },
  },
  build: {
    // Lowercase: Vite 8 minifies CSS with lightningcss, which rejects "ES2020".
    target: "es2020",
    outDir: "dist",
    sourcemap: false,
    minify: "terser",
  },
});
