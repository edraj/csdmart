import { describe, expect, it } from "vitest";
import { formatBytes, formatDate, formatDuration, formatNumber, relativeParts, toDate } from "./format";

// A fixed instant so every expectation is deterministic. Local time is used on
// purpose: dmart timestamps carry no zone and the UI shows them as local.
const AT = new Date(2026, 9, 8, 14, 5, 46, 485); // 8 Oct 2026 14:05:46.485

describe("toDate", () => {
    it("accepts ISO strings with microseconds, epoch numbers and Dates", () => {
        expect(toDate("2022-11-29T14:05:46.485769")?.getFullYear()).toBe(2022);
        expect(toDate(AT.getTime())?.getTime()).toBe(AT.getTime());
        expect(toDate(AT)).toBe(AT);
    });

    it("returns null for nothing and for garbage", () => {
        expect(toDate(null)).toBeNull();
        expect(toDate(undefined)).toBeNull();
        expect(toDate("")).toBeNull();
        expect(toDate("not a date")).toBeNull();
    });
});

describe("formatDate", () => {
    it("renders a medium date in the given locale", () => {
        expect(formatDate(AT, "date", "en")).toBe("Oct 8, 2026");
        expect(formatDate(AT, "date", "en-GB")).toBe("8 Oct 2026");
    });

    it("renders date and short time by default", () => {
        const out = formatDate(AT, "datetime", "en");
        expect(out).toContain("Oct 8, 2026");
        expect(out).toMatch(/2:05/);
        expect(formatDate(AT, undefined, "en")).toBe(out);
    });

    it("uses the locale's own script for Arabic", () => {
        expect(formatDate(AT, "date", "ar")).not.toBe("Oct 8, 2026");
        // The day period ("م") is Arabic text; digits follow the locale's
        // default numbering system, which the formatter does not force.
        expect(formatDate(AT, "datetime", "ar")).toMatch(/[؀-ۿ]/);
        expect(formatDate(AT, "date", "ar-EG")).toMatch(/[٠-٩]/);
    });

    it("never shows raw ISO text and is empty for no value", () => {
        const out = formatDate("2022-11-29T14:05:46.485769", "datetime", "en");
        expect(out).not.toContain("T14:05:46");
        expect(out).not.toContain("485769");
        expect(formatDate(null, "date", "en")).toBe("");
        expect(formatDate("garbage", "date", "en")).toBe("");
    });

    it("formats relative times against a given now", () => {
        const now = AT;
        const threeHoursAgo = new Date(AT.getTime() - 3 * 60 * 60 * 1000);
        const inTwoDays = new Date(AT.getTime() + 2 * 24 * 60 * 60 * 1000);
        expect(formatDate(threeHoursAgo, "relative", "en", now)).toBe("3 hours ago");
        expect(formatDate(inTwoDays, "relative", "en", now)).toBe("in 2 days");
        expect(formatDate(new Date(AT.getTime() - 24 * 60 * 60 * 1000), "relative", "en", now)).toBe("yesterday");
    });

    it("falls back to English when no locale is active", () => {
        expect(formatDate(AT, "date")).toBe("Oct 8, 2026");
    });
});

describe("relativeParts", () => {
    it("picks the largest whole unit", () => {
        expect(relativeParts(-120 * 60 * 1000)).toEqual({ value: -2, unit: "hour" });
        expect(relativeParts(-100 * 60 * 1000)).toEqual({ value: -2, unit: "hour" });
        expect(relativeParts(-59 * 60 * 1000)).toEqual({ value: -59, unit: "minute" });
        expect(relativeParts(-45 * 1000)).toEqual({ value: -45, unit: "second" });
        expect(relativeParts(8 * 24 * 60 * 60 * 1000)).toEqual({ value: 1, unit: "week" });
        expect(relativeParts(400 * 24 * 60 * 60 * 1000)).toEqual({ value: 1, unit: "year" });
    });
});

describe("formatNumber", () => {
    it("groups digits per locale", () => {
        expect(formatNumber(1234567, {}, "en")).toBe("1,234,567");
        expect(formatNumber("42", {}, "en")).toBe("42");
        expect(formatNumber(1234, {}, "ar-EG")).toMatch(/[٠-٩]/);
    });

    it("passes through what it cannot format", () => {
        expect(formatNumber(null, {}, "en")).toBe("");
        expect(formatNumber(undefined, {}, "en")).toBe("");
        expect(formatNumber("abc", {}, "en")).toBe("abc");
    });
});

describe("formatBytes / formatDuration", () => {
    it("scales units", () => {
        expect(formatBytes(512, "en")).toBe("512 B");
        expect(formatBytes(1536, "en")).toBe("1.5 KB");
        expect(formatBytes(5 * 1024 * 1024, "en")).toBe("5 MB");
        expect(formatBytes(-1, "en")).toBe("");
    });

    it("switches from ms to s at one second", () => {
        expect(formatDuration(350, "en")).toBe("350 ms");
        expect(formatDuration(2500, "en")).toBe("2.5 s");
        expect(formatDuration(NaN, "en")).toBe("");
    });
});
