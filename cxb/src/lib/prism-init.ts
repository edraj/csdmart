// Force the prismjs core CJS wrapper to invoke (the IIFE inside prism.js
// only runs when the module's exports are dereferenced). Touching
// `Prism.languages` does that and makes `_self.Prism = _` (i.e.
// `window.Prism`) take effect. The vite plugin in vite.config.ts also
// rewrites every `prismjs/components/prism-*.js` to add a real
// `import Prism from "prismjs"`, so the bare-`Prism` references in
// those addon files resolve through ESM rather than the broken global
// path under rolldown.

import Prism from "prismjs";

void Prism.languages;

if (typeof globalThis !== "undefined" && !(globalThis as { Prism?: unknown }).Prism) {
  (globalThis as { Prism: unknown }).Prism = Prism;
}

export default Prism;
