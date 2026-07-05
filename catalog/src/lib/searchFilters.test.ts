import { describe, expect, it } from "vitest";
import {
  buildFieldFilterClause,
  isSafeAlternationValue,
} from "./searchFilters";

describe("isSafeAlternationValue", () => {
  it("accepts plain identifiers, numbers, and hyphens", () => {
    for (const v of ["pending", "in-progress", "42", "-3.5", "a_b.c"]) {
      expect(isSafeAlternationValue(v)).toBe(true);
    }
  });

  it("rejects grammar metacharacters and whitespace", () => {
    for (const v of ["in progress", "a|b", "a(b)", "x:y", '"q"', "a@b", "<3", "", "a=b"]) {
      expect(isSafeAlternationValue(v)).toBe(false);
    }
  });
});

describe("buildFieldFilterClause", () => {
  it("emits paren-free alternation for safe values", () => {
    expect(buildFieldFilterClause("payload.body.status", ["pending", "done"])).toBe(
      "@payload.body.status:pending|done",
    );
    expect(buildFieldFilterClause("tags", ["a"])).toBe("@tags:a");
  });

  it("never emits the @field:(a|b) form the grammar tokenizes as an empty selector", () => {
    const clause = buildFieldFilterClause("payload.body.status", ["pending", "done"]);
    expect(clause).not.toContain(":(");
  });

  it("quotes values with spaces or metacharacters", () => {
    expect(buildFieldFilterClause("payload.body.status", ["in progress"])).toBe(
      '@payload.body.status:"in progress"',
    );
  });

  it("combines mixed safe/unsafe values with an explicit OR group", () => {
    expect(
      buildFieldFilterClause("payload.body.status", ["in progress", "done"]),
    ).toBe('(@payload.body.status:"in progress" or @payload.body.status:done)');
  });

  it("drops unrepresentable values and returns '' when none remain", () => {
    expect(buildFieldFilterClause("f", ['say "hi"', ""])).toBe("");
    expect(buildFieldFilterClause("f", ['say "hi"', "ok"])).toBe("@f:ok");
  });
});
