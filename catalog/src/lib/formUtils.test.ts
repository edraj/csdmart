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
