import { describe, expect, it } from "vitest";
import { clampPage, hasMoreRecords, pageCount } from "./paging";

describe("pageCount", () => {
    it("rounds up and never drops below one page", () => {
        expect(pageCount(0, 15)).toBe(1);
        expect(pageCount(15, 15)).toBe(1);
        expect(pageCount(16, 15)).toBe(2);
        expect(pageCount(1000, 100)).toBe(10);
    });

    it("tolerates garbage totals and limits", () => {
        expect(pageCount(null, 15)).toBe(1);
        expect(pageCount(undefined, 15)).toBe(1);
        expect(pageCount(-1, 15)).toBe(1);
        expect(pageCount(NaN, 15)).toBe(1);
        expect(pageCount(50, 0)).toBe(1);
        expect(pageCount(50, -5)).toBe(1);
    });
});

describe("clampPage", () => {
    it("sanitises a page that came from the URL", () => {
        expect(clampPage("3", 100, 15)).toBe(3);
        expect(clampPage("abc", 100, 15)).toBe(1);
        expect(clampPage(undefined, 100, 15)).toBe(1);
        expect(clampPage(0, 100, 15)).toBe(1);
        expect(clampPage(-4, 100, 15)).toBe(1);
        expect(clampPage(2.7, 100, 15)).toBe(2);
    });

    // The rows-per-page bug: page 10 of 15-per-page is page 2 of 100-per-page.
    it("caps at the last page once the total is known", () => {
        expect(clampPage(10, 150, 100)).toBe(2);
        expect(clampPage(10, 150, 15)).toBe(10);
        expect(clampPage(11, 150, 15)).toBe(10);
    });

    // Bulk-deleting the only row on the last page.
    it("steps back when the last page is now empty", () => {
        expect(clampPage(3, 30, 15)).toBe(2);
        expect(clampPage(2, 0, 15)).toBe(1);
    });

    it("only applies the lower bound while the total is unknown", () => {
        expect(clampPage(7, null, 15)).toBe(7);
        expect(clampPage("x", null, 15)).toBe(1);
        expect(clampPage(7, undefined, 15)).toBe(7);
    });
});

describe("hasMoreRecords", () => {
    it("trusts a counted total", () => {
        expect(hasMoreRecords(120, 50, 50, 50)).toBe(true);
        expect(hasMoreRecords(50, 50, 50, 50)).toBe(false);
        expect(hasMoreRecords(0, 0, 0, 50)).toBe(false);
    });

    it("falls back to a full page when the server did not count", () => {
        expect(hasMoreRecords(-1, 50, 50, 50)).toBe(true);
        expect(hasMoreRecords(undefined, 50, 50, 50)).toBe(true);
        expect(hasMoreRecords(-1, 37, 37, 50)).toBe(false);
        expect(hasMoreRecords(null, 0, 0, 50)).toBe(false);
        expect(hasMoreRecords(null, 10, 10, 0)).toBe(false);
    });
});
