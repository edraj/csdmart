import { _, addMessages, getLocaleFromNavigator, init, locale } from "svelte-i18n";
import { derived } from "svelte/store";
import { website } from "../config";

// Add all ./xx.json localizations here.
import ar from "./ar.json";
import en from "./en.json";

addMessages("ar", ar);
addMessages("en", en);

const bundled: Record<string, Record<string, string>> = { ar, en };

/** Locales this build ships messages for. */
const available_locales = Object.keys(bundled);

/**
 * Locales the operator enabled in config.json AND this build can render. The
 * language menu is built from this list, so a config that names a language
 * with no bundle (or a bundle the config does not enable) never shows up as
 * an option that would fall back to English.
 */
function enabledLocales(): string[] {
    return Object.keys(website.languages ?? {}).filter((code) => code in bundled);
}

/** The locale a first-time visitor gets when the browser offers no match. */
function defaultLocale(): string {
    const enabled = enabledLocales();
    const configured = website.default_language;
    if (configured && enabled.includes(configured)) return configured;
    return enabled[0] ?? "en";
}

const STORAGE_KEY = "preferred_locale";

function readStoredLocale(): string | null {
    if (typeof localStorage === "undefined") return null;
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        const parsed = raw ? JSON.parse(raw) : null;
        return typeof parsed === "string" ? parsed : null;
    } catch {
        return null;
    }
}

function storeLocale(code: string) {
    if (typeof localStorage === "undefined") return;
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(code));
    } catch {
        // Storage may be full or blocked; the in-memory locale still applies.
    }
}

function switchLocale(code: string) {
    const next = enabledLocales().includes(code) ? code : defaultLocale();
    storeLocale(next);
    locale.set(next);
}

/**
 * Stored preference → browser language → `website.default_language`. The old
 * version had the fallback check inverted (`fallback.trim().length > 0`), so a
 * first-time visitor whose browser spoke neither language always landed on
 * "en" regardless of the configured default.
 */
function getPreferredLocale(): string {
    const enabled = enabledLocales();

    const stored = readStoredLocale();
    if (stored && enabled.includes(stored)) return stored;

    const navigatorLocale = getLocaleFromNavigator();
    if (navigatorLocale) {
        const match = enabled.find((code) => navigatorLocale.toLowerCase().startsWith(code.toLowerCase()));
        if (match) return match;
    }

    return defaultLocale();
}

const rtl = new Set(["ar", "fa", "ur", "he"]);
const isRtl = (code: string | null | undefined) => rtl.has((code ?? "").split("-")[0]);

const dir = derived(locale, ($locale) => (isRtl($locale) ? "rtl" : "ltr"));
const isLocaleLoaded = derived(locale, ($locale) => typeof $locale === "string");

function setupI18n() {
    const initial = getPreferredLocale();
    storeLocale(initial);

    init({
        initialLocale: initial,
        fallbackLocale: defaultLocale(),
    });

    // <html lang dir> follow the locale: screen readers pick the right voice,
    // CSS logical properties and `rtl:` variants flip, and `:lang()` works.
    if (typeof document !== "undefined") {
        locale.subscribe((code) => {
            if (typeof code !== "string") return;
            document.documentElement.lang = code;
            document.documentElement.dir = isRtl(code) ? "rtl" : "ltr";
        });
    }
}

export { _, dir, setupI18n, locale, isLocaleLoaded, switchLocale, available_locales, enabledLocales };
