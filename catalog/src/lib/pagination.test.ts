import { describe, expect, it } from "vitest";
import { ELLIPSIS, pageRange, pageWindow } from "./pagination";

describe("pageWindow", () => {
  it("lists every page when there are few", () => {
    expect(pageWindow(1, 1)).toEqual([1]);
    expect(pageWindow(2, 3)).toEqual([1, 2, 3]);
    expect(pageWindow(4, 7)).toEqual([1, 2, 3, 4, 5, 6, 7]);
  });

  it("keeps first and last with an ellipsis on each side of the window", () => {
    expect(pageWindow(5, 20)).toEqual([1, ELLIPSIS, 4, 5, 6, ELLIPSIS, 20]);
  });

  it("drops the ellipsis next to the edges", () => {
    expect(pageWindow(1, 20)).toEqual([1, 2, ELLIPSIS, 20]);
    expect(pageWindow(2, 20)).toEqual([1, 2, 3, ELLIPSIS, 20]);
    expect(pageWindow(20, 20)).toEqual([1, ELLIPSIS, 19, 20]);
  });

  it("clamps an out-of-range current page", () => {
    expect(pageWindow(0, 20)).toEqual(pageWindow(1, 20));
    expect(pageWindow(99, 20)).toEqual(pageWindow(20, 20));
  });

  it("honours a wider sibling count", () => {
    expect(pageWindow(10, 20, 2)).toEqual([1, ELLIPSIS, 8, 9, 10, 11, 12, ELLIPSIS, 20]);
  });

  it("returns nothing for no pages", () => {
    expect(pageWindow(1, 0)).toEqual([]);
    expect(pageWindow(1, Number.NaN)).toEqual([]);
  });
});

describe("pageRange", () => {
  it("computes 1-based bounds", () => {
    expect(pageRange(1, 10, 35)).toEqual({ start: 1, end: 10 });
    expect(pageRange(4, 10, 35)).toEqual({ start: 31, end: 35 });
  });

  it("is zero when there is nothing to show", () => {
    expect(pageRange(1, 10, 0)).toEqual({ start: 0, end: 0 });
    expect(pageRange(1, 0, 10)).toEqual({ start: 0, end: 0 });
  });
});
