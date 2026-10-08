// Light / dark / system theme for the catalog.
//
// The CSS in app.css paints dark for `:root[data-theme="dark"]` and, before
// this module has run, for an OS that prefers dark (`prefers-color-scheme`)
// unless `data-theme="light"` is set. This module owns the attribute: it
// stores the visitor's preference, resolves "system" against the media query,
// writes the resolved value to <html data-theme> (so flowbite's `dark:`
// variant and the token overrides agree) and follows OS changes live while
// the preference is "system".

import { derived, get, writable } from "svelte/store";

export type ThemePreference = "light" | "dark" | "system";
export type ResolvedTheme = "light" | "dark";

export const THEME_STORAGE_KEY = "theme";
export const THEME_PREFERENCES: readonly ThemePreference[] = ["light", "dark", "system"];

export function isThemePreference(value: unknown): value is ThemePreference {
  return typeof value === "string" && (THEME_PREFERENCES as readonly string[]).includes(value);
}

/** Pure: what to paint for a preference given whether the OS prefers dark. */
export function resolveTheme(preference: ThemePreference, systemPrefersDark: boolean): ResolvedTheme {
  if (preference === "system") return systemPrefersDark ? "dark" : "light";
  return preference;
}

/** The preference stored by an earlier visit, or "system" when none/invalid. */
export function readStoredTheme(storage: Pick<Storage, "getItem"> | null | undefined): ThemePreference {
  try {
    const raw = storage?.getItem(THEME_STORAGE_KEY);
    return isThemePreference(raw) ? raw : "system";
  } catch {
    return "system";
  }
}

function systemPrefersDark(): boolean {
  return typeof window !== "undefined" && typeof window.matchMedia === "function"
    ? window.matchMedia("(prefers-color-scheme: dark)").matches
    : false;
}

const storageOrNull = (): Storage | null => (typeof localStorage !== "undefined" ? localStorage : null);

export const themePreference = writable<ThemePreference>(readStoredTheme(storageOrNull()));
const systemDark = writable<boolean>(systemPrefersDark());

export const resolvedTheme = derived([themePreference, systemDark], ([$pref, $dark]) =>
  resolveTheme($pref, $dark),
);

function apply(theme: ResolvedTheme) {
  if (typeof document === "undefined") return;
  document.documentElement.dataset.theme = theme;
}

/** Persist a preference and repaint. */
export function setTheme(preference: ThemePreference) {
  themePreference.set(preference);
  try {
    storageOrNull()?.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // Private mode / quota: the choice still applies for this page.
  }
  apply(get(resolvedTheme));
}

/** Cycle light → dark → system → light; handy for a single toggle button. */
export function nextTheme(current: ThemePreference): ThemePreference {
  const i = THEME_PREFERENCES.indexOf(current);
  return THEME_PREFERENCES[(i + 1) % THEME_PREFERENCES.length];
}

let initialised = false;

/**
 * Paint the stored preference and start following the OS while it is
 * "system". Call once, before the app mounts, so the first frame is right.
 */
export function initTheme() {
  if (initialised) return;
  initialised = true;
  apply(get(resolvedTheme));
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return;
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  const onChange = (e: MediaQueryListEvent) => {
    systemDark.set(e.matches);
    apply(get(resolvedTheme));
  };
  media.addEventListener("change", onChange);
}
