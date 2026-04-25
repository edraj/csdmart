import {defineConfig} from "vite";
import {mdsvex} from "mdsvex";
import routify from "@roxi/routify/vite-plugin";
import {svelte, vitePreprocess} from "@sveltejs/vite-plugin-svelte";
import {viteStaticCopy} from "vite-plugin-static-copy";
import plantuml from "@akebifiky/remark-simple-plantuml";
import svelteMd from "vite-plugin-svelte-md";
import tailwindcss from "@tailwindcss/vite"
import {execSync} from "node:child_process";
import type {Plugin} from "vite";

// `prismjs/components/prism-*.js` files reference a bare global `Prism`
// without importing it. Under rolldown they get bundled as side-effect
// modules whose top-level code runs before any importer body — so the
// global is undefined and the load throws. Prepending an explicit
// `import Prism from "prismjs"` turns the bare reference into a tracked
// ESM binding, which rolldown then orders correctly behind the core.
const prismAddonImportPlugin = (): Plugin => ({
  name: "cxb:prismjs-addon-import",
  enforce: "pre",
  transform(code, id) {
    if (/\/prismjs\/components\/prism-[^./]+\.js(?:[?#]|$)/.test(id) && !code.includes("import Prism")) {
      return {code: `import Prism from "prismjs";\n${code}`, map: null};
    }
  },
});

const production = process.env.NODE_ENV === "production";
const gitHash = (() => {
  try {
    return execSync("git rev-parse --short HEAD").toString().trim();
  } catch {
    return "unknown";
  }
})();

export default defineConfig({
  base: "./",
  clearScreen: false,
  define: {
    'import.meta.env.VITE_GIT_HASH': JSON.stringify(gitHash),
  },
  resolve: {
    alias: {
      "@": process.cwd() + "/src",
      "~": process.cwd() + "/node_modules",
    },
  },
  plugins: [
    prismAddonImportPlugin(),
    tailwindcss(),
    svelteMd(),
    viteStaticCopy({
      targets: [
        {
          src: 'public/config.json',
          dest: ''
        }
      ]
    }),
    routify({
      "render.ssr": {enable: false},
    }),
    svelte({
      compilerOptions: {
        dev: !production,
      },
      extensions: [".md", ".svelte"],
      preprocess: [
        vitePreprocess(),
        mdsvex({
          extension: "md",
          remarkPlugins: [
            plantuml, {
              baseUrl: "https://www.plantuml.com/plantuml/svg"
            }
          ],
        }) as any
      ],
      onwarn: (warning, defaultHandler) => {
        const ignoredWarnings = [
          'non_reactive_update',
          'state_referenced_locally',
          'element_invalid_self_closing_tag',
          'event_directive_deprecated',
          'css_unused_selector'
        ];
        if (
            warning.code?.startsWith("a11y") ||
            warning.filename?.startsWith("/node_modules") ||
            ignoredWarnings.includes(warning.code)
        )
          return;
        if (typeof defaultHandler !== "undefined") defaultHandler(warning);
      },
    }),
  ],
  build: {
    chunkSizeWarningLimit: 512,
    cssMinify: 'lightningcss',
    rollupOptions: {
      // commonJsVariableInEsm: noisy `module.exports` warning from
      //   @typewriter/delta — UMD-shaped ESM file we don't control.
      // pluginTimings: rolldown's per-build profile printout; useful when
      //   diagnosing slow builds, otherwise just chatter.
      checks: {
        commonJsVariableInEsm: false,
        pluginTimings: false,
      },
      output: {
        manualChunks(id) {
          if (id.includes('node_modules')) {
            const pkg = id.toString().split('node_modules/')[1].split('/')[0].toString();
            // Skip packages that produce empty chunks after tree-shaking
            const skipChunks = [
              '@popperjs', 'date-fns', 'fast-deep-equal', 'fast-uri',
              'jmespath', 'json-schema-traverse', 'jsonpath-plus'
            ];
            if (skipChunks.includes(pkg)) return;
            return pkg;
          }
        },
      },
    }
  },
  server: {port: 1337},
});