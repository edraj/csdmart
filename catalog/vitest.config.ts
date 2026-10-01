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
    // ssg/ is a plain Node ESM build tool (it must run without a bundler),
      // so its tests are .mjs and live beside it rather than under src/.
      include: ["src/**/*.{test,spec}.ts", "ssg/**/*.{test,spec}.mjs"],
    environment: "node",
  },
});
