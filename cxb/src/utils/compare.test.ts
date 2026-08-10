import { describe, expect, it } from "vitest";
import { isDeepEqual, removeEmpty } from "./compare";

/**
 * These two drive the entry editor: isDeepEqual decides whether the "unsaved
 * changes" state is dirty, and removeEmpty strips blanks out of a payload
 * before it is sent. Both are easy to get subtly wrong in ways that either
 * lose a user's edit or send a field the server then rejects.
 */
describe("isDeepEqual", () => {
  it("compares primitives by value", () => {
    expect(isDeepEqual(1, 1)).toBe(true);
    expect(isDeepEqual("a", "a")).toBe(true);
    expect(isDeepEqual(1, 2)).toBe(false);
    expect(isDeepEqual(1, "1")).toBe(false);
  });

  it("treats null and undefined as distinct", () => {
    expect(isDeepEqual(null, null)).toBe(true);
    expect(isDeepEqual(undefined, undefined)).toBe(true);
    expect(isDeepEqual(null, undefined)).toBe(false);
  });

  // null is typeof "object", so the null guards in the implementation are
  // load-bearing — without them this pair would walk into key enumeration.
  it("does not treat null as an empty object", () => {
    expect(isDeepEqual(null, {})).toBe(false);
    expect(isDeepEqual({}, null)).toBe(false);
  });

  it("compares flat objects by content, not identity", () => {
    expect(isDeepEqual({ a: 1, b: "x" }, { a: 1, b: "x" })).toBe(true);
    expect(isDeepEqual({ a: 1 }, { a: 2 })).toBe(false);
  });

  it("ignores key order", () => {
    expect(isDeepEqual({ a: 1, b: 2 }, { b: 2, a: 1 })).toBe(true);
  });

  it("detects a differing key count", () => {
    expect(isDeepEqual({ a: 1 }, { a: 1, b: 2 })).toBe(false);
    expect(isDeepEqual({ a: 1, b: 2 }, { a: 1 })).toBe(false);
  });

  it("detects a same-sized object with a different key name", () => {
    expect(isDeepEqual({ a: 1 }, { b: 1 })).toBe(false);
  });

  it("recurses into nested structures", () => {
    expect(isDeepEqual({ a: { b: { c: [1, 2] } } }, { a: { b: { c: [1, 2] } } })).toBe(true);
    expect(isDeepEqual({ a: { b: { c: [1, 2] } } }, { a: { b: { c: [1, 3] } } })).toBe(false);
  });

  it("compares arrays elementwise, including length and order", () => {
    expect(isDeepEqual([1, 2, 3], [1, 2, 3])).toBe(true);
    expect(isDeepEqual([1, 2], [1, 2, 3])).toBe(false);
    expect(isDeepEqual([1, 2], [2, 1])).toBe(false);
  });
});

describe("removeEmpty", () => {
  it("drops empty and whitespace-only strings", () => {
    expect(removeEmpty({ a: "keep", b: "", c: "   " })).toEqual({ a: "keep" });
  });

  it("drops null and undefined", () => {
    expect(removeEmpty({ a: 1, b: null, c: undefined })).toEqual({ a: 1 });
  });

  // Falsy-but-meaningful values: a naive `if (obj[key])` filter would eat
  // these, turning "0 items" or an explicit false into an absent field.
  it("keeps 0 and false", () => {
    expect(removeEmpty({ count: 0, enabled: false })).toEqual({
      count: 0,
      enabled: false,
    });
  });

  it("keeps empty arrays, which carry meaning as an explicit clear", () => {
    expect(removeEmpty({ tags: [] })).toEqual({ tags: [] });
  });

  it("recurses into nested objects", () => {
    expect(removeEmpty({ outer: { keep: "y", drop: "" } })).toEqual({
      outer: { keep: "y" },
    });
  });

  it("recurses into objects inside arrays", () => {
    expect(removeEmpty({ items: [{ keep: "y", drop: "  " }] })).toEqual({
      items: [{ keep: "y" }],
    });
  });

  it("leaves primitive array members untouched", () => {
    expect(removeEmpty({ items: [1, "", null] })).toEqual({ items: [1, "", null] });
  });

  it("accepts a top-level array", () => {
    expect(removeEmpty([{ a: "" }, { b: "x" }] as any)).toEqual([{}, { b: "x" }]);
  });

  it("leaves a nested object empty rather than removing it", () => {
    expect(removeEmpty({ outer: { inner: "" } })).toEqual({ outer: {} });
  });
});
