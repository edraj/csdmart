// Flat config (ESLint 10). `yarn lint` had crashed since the ESLint 10
// upgrade because there was no config at all. Scope: src/ only; generated
// routes (.routify/) and build output are ignored.
//
// Baseline policy: the recommended TypeScript + Svelte sets, with the rules
// the codebase does not yet satisfy (explicit any, unused catch bindings)
// reported as warnings so lint is red only for real defects — leftover
// console.log, undefined/unused symbols, Svelte compile-level mistakes.
import js from "@eslint/js";
import svelte from "eslint-plugin-svelte";
import globals from "globals";
import ts from "typescript-eslint";

export default ts.config(
  { ignores: [".routify/**", "dist/**", "node_modules/**", "ssg/**", "**/*.test.ts"] },
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
      "@typescript-eslint/no-explicit-any": "warn",
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" },
      ],
      "svelte/no-at-html-tags": "warn",
      // Off: every one of its 14 hits was correct code — a plain Map/Set built
      // inside a function or $derived.by to compute a value (never rendered),
      // non-reactive module caches, or a $state Set updated with the
      // `x = new Set(x)` reassignment idiom, which Svelte 5 tracks. The rule
      // cannot tell those from a mutated reactive collection.
      "svelte/prefer-svelte-reactivity": "off",
    },
  },
  {
    files: ["**/*.svelte"],
    languageOptions: { parserOptions: { parser: ts.parser } },
  },
);
