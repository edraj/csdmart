// The one date formatter.
//
// Thirteen components each carried their own formatDate/formatRelativeTime,
// with four different output styles, three hard-coded English fallbacks
// ("N/A", "Just now", "5m ago") and one that glued an Arabic suffix straight
// onto the number. Everything goes through Intl here, so the locale decides
// the order, the month names, the digits and the plural form of "minutes".
//
//   formatDate(value, "date",     locale)  ->  "8 Oct 2026"
//   formatDate(value, "datetime", locale)  ->  "8 Oct 2026, 14:05"
//   formatDate(value, "relative", locale)  ->  "5 minutes ago" / "yesterday" / a date past 30 days
//
// Returns "" for a missing or unparseable value so callers can supply their
// own i18n fallback (`formatDate(x, "date", $locale) || $_("common.not_available")`).

export type DateStyle = "date" | "datetime" | "relative";

const DATE: Intl.DateTimeFormatOptions = { year: "numeric", month: "short", day: "numeric" };
const DATETIME: Intl.DateTimeFormatOptions = { ...DATE, hour: "2-digit", minute: "2-digit" };

// Past this the relative wording stops being useful ("34 days ago") and the
// absolute date is what a reader wants.
const RELATIVE_LIMIT_SECONDS = 30 * 24 * 3600;

function toDate(value: unknown): Date | null {
  if (value === null || value === undefined || value === "") return null;
  const date = value instanceof Date ? value : new Date(value as string | number);
  return Number.isNaN(date.getTime()) ? null : date;
}

// svelte-i18n's `$locale` is `string | null | undefined` before init; Intl
// treats undefined as "the runtime default" but throws on "".
function intlLocale(locale: string | null | undefined): string | undefined {
  return locale ? locale : undefined;
}

export function formatDate(
  value: unknown,
  style: DateStyle = "date",
  locale?: string | null,
  now: Date = new Date(),
): string {
  const date = toDate(value);
  if (!date) return "";
  const loc = intlLocale(locale);

  if (style === "relative") {
    const seconds = Math.round((now.getTime() - date.getTime()) / 1000);
    if (Math.abs(seconds) < RELATIVE_LIMIT_SECONDS) {
      return formatRelative(seconds, loc);
    }
    return new Intl.DateTimeFormat(loc, DATE).format(date);
  }

  return new Intl.DateTimeFormat(loc, style === "datetime" ? DATETIME : DATE).format(date);
}

// `seconds` is how long ago (positive = past). `numeric: "auto"` gives
// "yesterday"/"now" where the locale has such words.
function formatRelative(seconds: number, locale: string | undefined): string {
  const rtf = new Intl.RelativeTimeFormat(locale, { numeric: "auto" });
  const abs = Math.abs(seconds);
  const sign = seconds > 0 ? -1 : 1;
  if (abs < 60) return rtf.format(0, "second");
  if (abs < 3600) return rtf.format(sign * Math.floor(abs / 60), "minute");
  if (abs < 86400) return rtf.format(sign * Math.floor(abs / 3600), "hour");
  return rtf.format(sign * Math.floor(abs / 86400), "day");
}
