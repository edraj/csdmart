import {_, addMessages, date, getLocaleFromNavigator, init, locale, number, time,} from "svelte-i18n";
import {website} from "@/config";
import {derived} from "svelte/store";
import {resolvePreferredLocale} from "@/lib/preferredLocale";
import ar from "./ar.json";
import en from "./en.json";
import ku from "./ku.json";

addMessages("ar", ar);
addMessages("en", en);
addMessages("ku", ku);

const l17ns = { ar: ar, en: en, ku: ku };
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
 * keeps <html lang dir> in step with every later switch.
 */
function setupI18n() {
  let _locale: string = getPreferredLocale();

  if (!(_locale in l17ns) && website.default_language) {
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
  switchLocale,
  available_locales,
  FALLBACK_LOCALE,
};
