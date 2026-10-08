import { get } from "svelte/store";
import { locale } from "svelte-i18n";

/**
 * The one date formatter for the UI. Every date the user sees goes through
 * here, driven by the active svelte-i18n locale, so a timestamp never shows as
 * raw ISO text and the day/month order follows the language rather than the
 * browser.
 *
 *   "date"      → 8 Oct 2026                 (medium date)
 *   "datetime"  → 8 Oct 2026, 14:05          (medium date + short time)
 *   "relative"  → 3 hours ago / in 2 days    (Intl.RelativeTimeFormat)
 *
 * Arabic digits are fine: the locale decides. `localeOverride` exists for
 * callers outside a Svelte context (tests, workers).
 */
export type DateStyle = "date" | "datetime" | "relative";

export type DateInput = string | number | Date | null | undefined;

const FALLBACK_LOCALE = "en";

function currentLocale(override?: string): string {
    if (override) return override;
    const current = get(locale);
    return typeof current === "string" && current.length > 0 ? current : FALLBACK_LOCALE;
}

/**
 * Parse anything the API hands back into a Date, or null when it is not a
 * date at all. dmart timestamps come as ISO strings with microseconds and no
 * zone (`2022-11-29T14:05:46.485769`); `new Date` accepts those as local time.
 */
export function toDate(value: DateInput): Date | null {
    if (value === null || value === undefined || value === "") return null;
    const date = value instanceof Date ? value : new Date(value);
    return Number.isNaN(date.getTime()) ? null : date;
}

const RELATIVE_UNITS: Array<{ unit: Intl.RelativeTimeFormatUnit; ms: number }> = [
    { unit: "year", ms: 365 * 24 * 60 * 60 * 1000 },
    { unit: "month", ms: 30 * 24 * 60 * 60 * 1000 },
    { unit: "week", ms: 7 * 24 * 60 * 60 * 1000 },
    { unit: "day", ms: 24 * 60 * 60 * 1000 },
    { unit: "hour", ms: 60 * 60 * 1000 },
    { unit: "minute", ms: 60 * 1000 },
    { unit: "second", ms: 1000 },
];

/**
 * Pick the largest unit whose magnitude is at least one, so "90 minutes ago"
 * reads as "1 hour ago" and anything under a minute as "now"-ish seconds.
 */
export function relativeParts(diffMs: number): { value: number; unit: Intl.RelativeTimeFormatUnit } {
    const abs = Math.abs(diffMs);
    for (const { unit, ms } of RELATIVE_UNITS) {
        if (abs >= ms) {
            return { value: Math.round(diffMs / ms), unit };
        }
    }
    return { value: Math.round(diffMs / 1000), unit: "second" };
}

export function formatDate(
    value: DateInput,
    style: DateStyle = "datetime",
    localeOverride?: string,
    now: Date = new Date(),
): string {
    const date = toDate(value);
    if (!date) return "";
    const loc = currentLocale(localeOverride);

    if (style === "relative") {
        const { value: amount, unit } = relativeParts(date.getTime() - now.getTime());
        return new Intl.RelativeTimeFormat(loc, { numeric: "auto" }).format(amount, unit);
    }

    const options: Intl.DateTimeFormatOptions =
        style === "date"
            ? { dateStyle: "medium" }
            : { dateStyle: "medium", timeStyle: "short" };
    return new Intl.DateTimeFormat(loc, options).format(date);
}

/** Locale-aware integer/decimal formatting ("1,234" / "١٬٢٣٤"). */
export function formatNumber(
    value: number | string | null | undefined,
    options: Intl.NumberFormatOptions = {},
    localeOverride?: string,
): string {
    if (value === null || value === undefined || value === "") return "";
    const n = typeof value === "number" ? value : Number(value);
    if (!Number.isFinite(n)) return String(value);
    return new Intl.NumberFormat(currentLocale(localeOverride), options).format(n);
}

/** Bytes → "1.5 MB" in the active locale. */
export function formatBytes(bytes: number, localeOverride?: string): string {
    if (!Number.isFinite(bytes) || bytes < 0) return "";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let value = bytes;
    let i = 0;
    while (value >= 1024 && i < units.length - 1) {
        value /= 1024;
        i++;
    }
    const digits = i === 0 ? 0 : 2;
    return `${formatNumber(value, { maximumFractionDigits: digits }, localeOverride)} ${units[i]}`;
}

/** Milliseconds → "350 ms" / "2.5 s" in the active locale. */
export function formatDuration(ms: number, localeOverride?: string): string {
    if (!Number.isFinite(ms) || ms < 0) return "";
    if (ms < 1000) return `${formatNumber(Math.round(ms), {}, localeOverride)} ms`;
    return `${formatNumber(ms / 1000, { maximumFractionDigits: 1 }, localeOverride)} s`;
}
