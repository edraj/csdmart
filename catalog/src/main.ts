import { mount } from "svelte";
import App from "./App.svelte";
import "./app.css";
import { loadFontsLazily } from "./lib/performance";
import { configReady } from "./config";
import { initTheme } from "./lib/theme";

// Paint the stored light/dark/system choice before anything renders. The
// server's CSP (script-src 'self') rules out an inline <script> in index.html,
// so this is the earliest point; app.css covers the OS-dark case before it.
initTheme();

// The page ships no server-rendered markup (render.ssr is off in
// vite.config.ts), so this is a plain client mount, not a hydration.
configReady.then(() => {
  mount(App, { target: document.body });
  loadFontsLazily();
});
