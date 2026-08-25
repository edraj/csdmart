import { describe, expect, it } from "vitest";
import { isTotalUnknown, resolveTotal } from "@shared/query-total";

/**
 * The -1 sentinel is the whole reason these exist. A regression here is silent:
 * the UI renders a negative page count rather than throwing, so nothing in CI
 * or in a smoke test would notice.
 */
describe("resolveTotal", () => {
  it("passes real counts through, including zero", () => {
    expect(resolveTotal(0)).toBe(0);
    expect(resolveTotal(1)).toBe(1);
    expect(resolveTotal(2589782)).toBe(2589782);
  });

  it("maps the -1 skipped-count sentinel to the fallback", () => {
    expect(resolveTotal(-1)).toBe(0);
    expect(resolveTotal(-1, 15)).toBe(15);
  });

  it("does not let the sentinel survive the idioms it was reaching through", () => {
    // Typed as the API shape rather than the literal -1, so the compiler does
    // not fold these away as unreachable — the point is what happens at run
    // time to a value that arrived over the wire.
    const fromApi: number | null | undefined = -1;
    // `?? 0` misses it: -1 is neither null nor undefined.
    expect(fromApi ?? 0).toBe(-1);
    expect(resolveTotal(fromApi, 0)).toBe(0);
    // `|| records.length` misses it: -1 is truthy.
    expect(fromApi || 42).toBe(-1);
    expect(resolveTotal(fromApi, 42)).toBe(42);
  });

  it("treats missing and unparseable values as unknown", () => {
    expect(resolveTotal(undefined, 7)).toBe(7);
    expect(resolveTotal(null, 7)).toBe(7);
    expect(resolveTotal("nonsense", 7)).toBe(7);
    expect(resolveTotal(NaN, 7)).toBe(7);
  });

  it("does not mistake an absent total for a real zero", () => {
    // Number(null) and Number("") are both 0 — the trap this guards.
    expect(Number(null)).toBe(0);
    expect(resolveTotal(null, 7)).toBe(7);
    expect(resolveTotal("", 7)).toBe(7);
    expect(isTotalUnknown(null)).toBe(true);
  });

  it("accepts a numeric string, since JSON shapes vary", () => {
    expect(resolveTotal("12")).toBe(12);
  });
});

describe("isTotalUnknown", () => {
  it("separates not-counted from counted-as-zero", () => {
    expect(isTotalUnknown(-1)).toBe(true);
    expect(isTotalUnknown(0)).toBe(false);
    expect(isTotalUnknown(10)).toBe(false);
    expect(isTotalUnknown(undefined)).toBe(true);
  });
});
