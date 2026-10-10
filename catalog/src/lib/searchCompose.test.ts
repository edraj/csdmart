import { describe, expect, it } from "vitest";
import { andSearch, balanceParens } from "@shared/search-compose";
import { mergeSearch } from "@/lib/dmart_services/spaces";

describe("andSearch", () => {
  it("returns a lone fragment as it is", () => {
    expect(andSearch("hello world")).toBe("hello world");
    expect(andSearch("  hello ", "", null, undefined, "   ")).toBe("hello");
    expect(andSearch()).toBe("");
  });

  it("groups each fragment so an `or` in typed text cannot escape the others", () => {
    // Joined by spaces this was `x or y -@shortname:schema`: AND binds
    // tighter, so only `y` was filtered and the schema came back through `x`.
    expect(andSearch("x or y", "-@shortname:schema")).toBe("(x or y) (-@shortname:schema)");
  });

  it("balances each fragment so a stray paren cannot break the grouping", () => {
    expect(andSearch("x) or y", "@is_active:true")).toBe("(x or y) (@is_active:true)");
    expect(andSearch("(x or y", "@is_active:true")).toBe("((x or y)) (@is_active:true)");
  });
});

describe("balanceParens", () => {
  it("drops unmatched closers and closes open groups", () => {
    expect(balanceParens("a) b")).toBe("a b");
    expect(balanceParens("(a (b")).toBe("(a (b))");
    expect(balanceParens("(a) (b)")).toBe("(a) (b)");
  });

  it("leaves parentheses inside quotes alone", () => {
    expect(balanceParens('@payload.body.name:"Pad (Blue)"')).toBe('@payload.body.name:"Pad (Blue)"');
    expect(balanceParens('@payload.body.name:"a)"')).toBe('@payload.body.name:"a)"');
  });
});

describe("mergeSearch", () => {
  it("is andSearch", () => {
    expect(mergeSearch("a or b", "-@shortname:x|y", "", "@is_active:true")).toBe(
      "(a or b) (-@shortname:x|y) (@is_active:true)",
    );
    expect(mergeSearch("only")).toBe("only");
  });
});
