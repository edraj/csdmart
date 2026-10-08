import { describe, expect, it } from "vitest";
import { pageTitle, TITLE_SEPARATOR } from "./title";

describe("pageTitle", () => {
  it("joins page, context and site title with the separator", () => {
    expect(pageTitle(["My post", "news"], "dmart")).toBe(`My post${TITLE_SEPARATOR}news${TITLE_SEPARATOR}dmart`);
  });

  it("drops empty, blank, null and undefined parts", () => {
    expect(pageTitle(["", "  ", null, undefined, "Help"], "dmart")).toBe(`Help${TITLE_SEPARATOR}dmart`);
  });

  it("falls back to the site title alone", () => {
    expect(pageTitle([], "dmart")).toBe("dmart");
  });

  it("uses website.title by default", () => {
    // config.ts's default title before config.json is loaded.
    expect(pageTitle(["Login"])).toMatch(/^Login · .+/);
  });

  it("trims whitespace around parts", () => {
    expect(pageTitle(["  Login  "], " dmart ")).toBe(`Login${TITLE_SEPARATOR}dmart`);
  });
});
