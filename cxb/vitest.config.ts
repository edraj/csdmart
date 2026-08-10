import { defineConfig } from "vitest/config";
import * as path from "path";

// Mirrors catalog/vitest.config.ts deliberately — the two suites should be
// invoked and configured the same way, so `yarn --cwd <app> test` behaves
// identically for either frontend.
//
// The aliases match cxb/vite.config.ts. They are repeated rather than imported
// from it because that config pulls in routify, tailwind and the prism/rolldown
// workarounds, none of which a unit test needs and all of which would have to
// load before a single assertion ran.
export default defineConfig({
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "src"),
      "@shared": path.resolve(__dirname, "..", "ui-shared"),
    },
  },
  test: {
    include: ["src/**/*.{test,spec}.ts"],
    // Default to node; the files that need browser globals (localStorage,
    // document) opt in per-file with `// @vitest-environment jsdom`, which
    // keeps the majority of the suite from paying for a DOM it never touches.
    environment: "node",
  },
});
