// Flat config (ESLint 10). Scope: src/ only; generated routes (.routify/)
// and build output are ignored.
//
// Policy: the recommended TypeScript + Svelte sets, and lint is red only for
// real defects — leftover console.log, undefined/unused symbols, Svelte
// compile-level mistakes, and an explicit `any`. The code is fully typed
// (SDK types, the app's own shapes, `unknown` plus a guard for dynamic JSON),
// so a new `any` is an error, not a debt to carry. `svelte/no-at-html-tags`
// stays a warning: each `{@html}` site renders sanitised markup and says so
// inline.
import js from "@eslint/js";
import svelte from "eslint-plugin-svelte";
import globals from "globals";
import ts from "typescript-eslint";

export default ts.config(
  { ignores: [".routify/**", "dist/**", "node_modules/**", "**/*.test.ts"] },
  js.configs.recommended,
  ...ts.configs.recommended,
  ...svelte.configs["flat/recommended"],
  {
    languageOptions: {
      globals: { ...globals.browser, ...globals.node },
    },
    rules: {
      "no-console": ["error", { allow: ["warn", "error"] }],
      "no-empty": ["error", { allowEmptyCatch: true }],
      "@typescript-eslint/no-explicit-any": "error",
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" },
      ],
      "svelte/no-at-html-tags": "warn",
    },
  },
  {
    // `.svelte.ts` / `.svelte.js` modules are parsed by the Svelte parser too
    // (the recommended set routes them there); without the TS sub-parser the
    // rune files fail to parse at all.
    files: ["**/*.svelte", "**/*.svelte.ts", "**/*.svelte.js"],
    languageOptions: { parserOptions: { parser: ts.parser } },
  },
);
