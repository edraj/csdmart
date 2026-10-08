import { mount } from "svelte";
import App from "./App.svelte";
import "./app.css";
import { loadFontsLazily } from "./lib/performance";
import { configReady } from "./config";
import { initTheme } from "./lib/theme";
import { setupI18n } from "./i18n";

// Paint the stored light/dark/system choice before anything renders. The
// server's CSP (script-src 'self') rules out an inline <script> in index.html,
// so this is the earliest point; app.css covers the OS-dark case before it.
initTheme();

// The page ships no server-rendered markup (render.ssr is off in
// vite.config.ts), so this is a plain client mount, not a hydration.
// Config first (setupI18n reads website.languages / default_language), then
// the locale bundle, then the app — so the first paint has its strings.
configReady.then(setupI18n).then(() => {
  mount(App, { target: document.body });
  loadFontsLazily();
});
