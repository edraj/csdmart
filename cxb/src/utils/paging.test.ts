import { describe, expect, it } from "vitest";
import { clampPage, hasMoreRecords, pageCount, pageRange, visiblePages } from "./paging";

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

describe("visiblePages", () => {
    it("centres the window on the current page", () => {
        expect(visiblePages(5, 10, 5)).toEqual([3, 4, 5, 6, 7]);
        expect(visiblePages(6, 10, 4)).toEqual([4, 5, 6, 7]);
    });

    it("keeps the window full at both ends", () => {
        expect(visiblePages(1, 10, 5)).toEqual([1, 2, 3, 4, 5]);
        expect(visiblePages(2, 10, 5)).toEqual([1, 2, 3, 4, 5]);
        expect(visiblePages(10, 10, 5)).toEqual([6, 7, 8, 9, 10]);
        expect(visiblePages(9, 10, 5)).toEqual([6, 7, 8, 9, 10]);
    });

    it("shows every page when there are fewer than the window", () => {
        expect(visiblePages(1, 1, 5)).toEqual([1]);
        expect(visiblePages(2, 3, 5)).toEqual([1, 2, 3]);
    });

    it("tolerates a current page outside the range and odd inputs", () => {
        expect(visiblePages(0, 3, 5)).toEqual([1, 2, 3]);
        expect(visiblePages(99, 3, 5)).toEqual([1, 2, 3]);
        expect(visiblePages(1, 0, 5)).toEqual([1]);
        expect(visiblePages(1, NaN, 5)).toEqual([1]);
        expect(visiblePages(3, 10, 0)).toEqual([3]);
    });
});

describe("pageRange", () => {
    it("reports the rows a page shows", () => {
        expect(pageRange(1, 15, 42)).toEqual({ from: 1, to: 15 });
        expect(pageRange(2, 15, 42)).toEqual({ from: 16, to: 30 });
        expect(pageRange(3, 15, 42)).toEqual({ from: 31, to: 42 });
    });

    it("is empty for an empty list or a broken page size", () => {
        expect(pageRange(1, 15, 0)).toEqual({ from: 0, to: 0 });
        expect(pageRange(1, 0, 10)).toEqual({ from: 0, to: 0 });
        expect(pageRange(1, 15, -1)).toEqual({ from: 0, to: 0 });
    });

    it("never points past the total", () => {
        expect(pageRange(10, 15, 42)).toEqual({ from: 42, to: 42 });
        expect(pageRange(0, 15, 42)).toEqual({ from: 1, to: 15 });
    });
});
