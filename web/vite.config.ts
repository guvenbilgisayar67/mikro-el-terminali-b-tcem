import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Derlenen panel API'nin wwwroot klasörüne yazılır; API aynı adresten sunar.
export default defineConfig({
  plugins: [react()],
  build: { outDir: "../src/Butcem.Api/wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": "http://localhost:5080" } },
});
