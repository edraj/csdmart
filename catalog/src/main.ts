import { mount } from "svelte";
import App from "./App.svelte";
import "./app.css";
import { loadFontsLazily } from "./lib/performance";
import { configReady } from "./config";

// The page ships no server-rendered markup (render.ssr is off in
// vite.config.ts), so this is a plain client mount, not a hydration.
configReady.then(() => {
  mount(App, { target: document.body });
  loadFontsLazily();
});
