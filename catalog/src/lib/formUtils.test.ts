import { describe, expect, it } from "vitest";
import { pruneEmptyFormValues } from "./formUtils";

describe("pruneEmptyFormValues", () => {
  it("drops empty strings, null, and undefined", () => {
    expect(
      pruneEmptyFormValues({ a: "", b: "   ", c: null, d: undefined, e: "x" }),
    ).toEqual({ e: "x" });
  });

  it("returns undefined when everything is empty", () => {
    expect(pruneEmptyFormValues({ a: "", b: null, c: [], d: {} })).toBeUndefined();
    expect(pruneEmptyFormValues("")).toBeUndefined();
    expect(pruneEmptyFormValues(null)).toBeUndefined();
    expect(pruneEmptyFormValues([])).toBeUndefined();
    expect(pruneEmptyFormValues({})).toBeUndefined();
  });

  it("keeps zero, negative numbers, and both boolean states", () => {
    expect(pruneEmptyFormValues({ n: 0, m: -1, t: true, f: false })).toEqual({
      n: 0,
      m: -1,
      t: true,
      f: false,
    });
  });

  it("drops NaN produced by empty number inputs", () => {
    expect(pruneEmptyFormValues({ n: NaN, m: 3 })).toEqual({ m: 3 });
  });

  it("prunes arrays and drops items that become empty", () => {
    expect(pruneEmptyFormValues({ tags: ["a", "", "  ", "b"] })).toEqual({
      tags: ["a", "b"],
    });
    expect(pruneEmptyFormValues({ tags: ["", null] })).toBeUndefined();
  });

  // typeof is 'object' for these too, and Object.keys() on any of them is [],
  // so walking them as plain objects returned undefined and dropped the value
  // from the payload with no error. They are values, not containers of form
  // fields, and must survive untouched.
  it("keeps Date, File, Map and Set instead of walking them", () => {
    const date = new Date("2026-01-02T03:04:05Z");
    const map = new Map([["k", "v"]]);
    const set = new Set(["a"]);
    expect(pruneEmptyFormValues({ date, map, set })).toEqual({ date, map, set });
  });

  it("keeps a class instance whole", () => {
    class Point {
      constructor(
        public x: number,
        public y: number,
      ) {}
    }
    const p = new Point(1, 2);
    expect(pruneEmptyFormValues({ p })).toEqual({ p });
  });

  it("prunes nested objects recursively", () => {
    expect(
      pruneEmptyFormValues({
        person: { name: "sam", nickname: "", address: { street: "", city: "" } },
        meta: {},
      }),
    ).toEqual({ person: { name: "sam" } });
  });

  it("prunes objects inside arrays", () => {
    expect(
      pruneEmptyFormValues({
        items: [
          { label: "one", note: "" },
          { label: "", note: "" },
        ],
      }),
    ).toEqual({ items: [{ label: "one" }] });
  });

  it("preserves filled values untouched", () => {
    const data = {
      title: "hello",
      count: 5,
      active: true,
      list: [1, 2],
      nested: { a: "b" },
    };
    expect(pruneEmptyFormValues(data)).toEqual(data);
  });
});
