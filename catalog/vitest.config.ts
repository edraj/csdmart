import { defineConfig } from "vitest/config";
import * as path from "path";

export default defineConfig({
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "src"),
      // Mirrors vite.config.ts. cxb has no test runner, so the shared module's
      // tests run from here and cover both frontends.
      "@shared": path.resolve(__dirname, "..", "ui-shared"),
    },
  },
  test: {
    include: ["src/**/*.{test,spec}.ts"],
    environment: "node",
  },
});
