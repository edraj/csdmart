import { describe, expect, it } from "vitest";
import { formatDate } from "./format";

const NOW = new Date("2026-10-08T12:00:00Z");

describe("formatDate", () => {
  it("returns '' for missing or unparseable input", () => {
    expect(formatDate(undefined, "date", "en")).toBe("");
    expect(formatDate(null, "date", "en")).toBe("");
    expect(formatDate("", "date", "en")).toBe("");
    expect(formatDate("not a date", "date", "en")).toBe("");
    expect(formatDate("2026-99-99", "relative", "en")).toBe("");
  });

  it("formats a date in the locale's order and month names", () => {
    const iso = "2026-10-08T09:30:00Z";
    expect(formatDate(iso, "date", "en-GB")).toBe("8 Oct 2026");
    expect(formatDate(iso, "date", "en-US")).toBe("Oct 8, 2026");
    // Arabic: Arabic month name present; Intl decides digits and order.
    expect(formatDate(iso, "date", "ar")).toContain("أكتوبر");
  });

  it("accepts Date objects and epoch milliseconds", () => {
    const d = new Date("2026-01-02T00:00:00Z");
    expect(formatDate(d, "date", "en-GB")).toBe(formatDate(d.getTime(), "date", "en-GB"));
  });

  it("datetime adds hours and minutes", () => {
    const out = formatDate("2026-10-08T09:30:00Z", "datetime", "en-GB");
    expect(out).toContain("2026");
    expect(out).toMatch(/\d{2}:\d{2}/);
  });

  // An empty locale string throws in Intl; svelte-i18n's $locale is null
  // before init. Both must fall back to the runtime default, not throw.
  it("tolerates an unset locale", () => {
    expect(() => formatDate("2026-10-08T09:30:00Z", "date", "")).not.toThrow();
    expect(() => formatDate("2026-10-08T09:30:00Z", "date", null)).not.toThrow();
    expect(formatDate("2026-10-08T09:30:00Z", "date", undefined)).not.toBe("");
  });

  describe("relative", () => {
    const ago = (seconds: number) => new Date(NOW.getTime() - seconds * 1000);

    it("uses the locale's relative wording with proper plurals", () => {
      expect(formatDate(ago(10), "relative", "en", NOW)).toBe("now");
      expect(formatDate(ago(60), "relative", "en", NOW)).toBe("1 minute ago");
      expect(formatDate(ago(5 * 60), "relative", "en", NOW)).toBe("5 minutes ago");
      expect(formatDate(ago(2 * 3600), "relative", "en", NOW)).toBe("2 hours ago");
      expect(formatDate(ago(86400), "relative", "en", NOW)).toBe("yesterday");
      expect(formatDate(ago(3 * 86400), "relative", "en", NOW)).toBe("3 days ago");
    });

    // The bug this replaces: "5ساعات مضت" with the number glued to the suffix.
    // Intl spaces and pluralises it ("قبل ٥ ساعات").
    it("spaces and pluralises Arabic", () => {
      const out = formatDate(ago(5 * 3600), "relative", "ar", NOW);
      expect(out).toContain("ساعات");
      expect(out).not.toMatch(/\dساعات/);
    });

    it("handles a timestamp slightly in the future (clock skew) without 'ago'", () => {
      expect(formatDate(ago(-90), "relative", "en", NOW)).toBe("in 1 minute");
    });

    it("falls back to the absolute date beyond 30 days", () => {
      const out = formatDate(ago(45 * 86400), "relative", "en-GB", NOW);
      expect(out).toBe("24 Aug 2026");
    });
  });
});
