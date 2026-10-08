import {_, date, getLocaleFromNavigator, init, isLoading, locale, number, register, time, waitLocale,} from "svelte-i18n";
import {website} from "@/config";
import {derived} from "svelte/store";
import {resolvePreferredLocale} from "@/lib/preferredLocale";
// Each locale is its own chunk, fetched when first needed. Importing all
// three statically put ~298 kB of translations into the entry chunk of every
// cold load (en 70 kB, ar 92 kB, ku 136 kB minified) for a visitor who reads
// exactly one of them.
register("ar", () => import("./ar.json"));
register("en", () => import("./en.json"));
register("ku", () => import("./ku.json"));

const available_locales = ["ar", "en", "ku"];

// English is the fallback for a key missing from the active locale: it is the
// source language every key is written in first. (It used to be the
// deployment's default_language, so a Kurdish visitor saw Arabic for any gap.)
const FALLBACK_LOCALE = "en";

const rtl = ["ar", "ku"]; // Arabic, Kurdish (Sorani)

function directionOf(l: string | null | undefined): "rtl" | "ltr" {
  return l && rtl.includes(l) ? "rtl" : "ltr";
}

/**
 * Switches the application locale reactively (no page reload).
 * Persists the preference, updates the stored user locale, and lets
 * the locale/dir stores propagate the change through the UI.
 * @param _locale - The locale code to switch to (e.g., 'ar', 'en', 'ku')
 */
function switchLocale(_locale: string) {
  if (!(_locale in website.languages)) {
    _locale = website.default_language;
  }
  if (typeof localStorage !== "undefined") {
    localStorage.setItem("preferred_locale", JSON.stringify(_locale));

    const userData = localStorage.getItem("user");
    if (userData) {
      try {
        const user = JSON.parse(userData);
        if (user && typeof user === "object") {
          user.locale = _locale;
          localStorage.setItem("user", JSON.stringify(user));
        }
      } catch {
        // Ignore parse errors
      }
    }
  }
  locale.set(_locale);
}

/**
 * Determines the locale to start in: an earlier explicit choice, else the
 * deployment's default_language, else the browser language, else English.
 * Nothing is persisted here — only switchLocale records a choice, so a later
 * change to default_language still applies to visitors who never picked one.
 */
function getPreferredLocale(): string {
  const stored =
    typeof localStorage !== "undefined"
      ? localStorage.getItem("preferred_locale")
      : null;
  return resolvePreferredLocale({
    stored,
    defaultLanguage: website.default_language,
    languages: website.languages,
    navigatorLocale: getLocaleFromNavigator(),
  });
}

let documentSynced = false;

/**
 * Initializes the internationalization system with the preferred locale and
 * keeps <html lang dir> in step with every later switch. Resolves once the
 * initial locale (and its fallback) have loaded — mount the app after that,
 * or the first paint shows raw keys.
 */
function setupI18n(): Promise<void> {
  let _locale: string = getPreferredLocale();

  if (!available_locales.includes(_locale) && website.default_language) {
    _locale = website.default_language;
  }

  init({
    initialLocale: _locale,
    fallbackLocale: FALLBACK_LOCALE,
  });

  if (!documentSynced && typeof document !== "undefined") {
    documentSynced = true;
    locale.subscribe(($locale) => {
      if (!$locale) return;
      document.documentElement.lang = $locale;
      document.documentElement.dir = directionOf($locale);
    });
  }
  return waitLocale();
}

const dir = derived(locale, ($locale) => directionOf($locale));
// Twenty-five components used to derive this themselves from `locale`, each
// with its own copy of the RTL list. One place, driven by `dir`.
const isRTL = derived(dir, ($dir) => $dir === "rtl");
const isLocaleLoaded = derived(
  locale,
  ($locale) => typeof $locale === "string"
);

export {
  _,
  dir,
  isRTL,
  setupI18n,
  time,
  date,
  number,
  locale,
  isLocaleLoaded,
  isLoading,
  switchLocale,
  available_locales,
  FALLBACK_LOCALE,
};
