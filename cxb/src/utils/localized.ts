import { get } from "svelte/store";
import { locale } from "svelte-i18n";

/**
 * A dmart translatable field: `{ en: "...", ar: "..." }`, sometimes a bare
 * string, often missing. Pick the active locale's text, then any language
 * that has one, then the fallback (usually the shortname).
 */
export type LocalizedValue = Record<string, string | null | undefined> | string | null | undefined;

export function localizedText(value: LocalizedValue, fallback = "", localeOverride?: string): string {
    if (value === null || value === undefined) return fallback;
    if (typeof value === "string") return value.trim() ? value : fallback;
    if (typeof value !== "object") return fallback;

    const active = localeOverride ?? (get(locale) || "");
    const candidates = [active, active.split("-")[0], "en", "ar", ...Object.keys(value)];
    for (const key of candidates) {
        if (!key) continue;
        const text = value[key];
        if (typeof text === "string" && text.trim()) return text;
    }
    return fallback;
}
