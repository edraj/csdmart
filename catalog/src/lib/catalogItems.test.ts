import { describe, expect, it } from "vitest";
import {
  attachmentCounts,
  findUserReactionId,
  folderNameOf,
  isHot,
  itemDescription,
  itemTitle,
  localized,
  previewText,
  tagsOf,
} from "./catalogItems";

describe("localized", () => {
  it("prefers the active locale, then en, then ar, then anything", () => {
    const v = { en: "English", ar: "عربي", ku: "کوردی" };
    expect(localized(v, "ku")).toBe("کوردی");
    expect(localized(v, "fr")).toBe("English");
    expect(localized({ ar: "عربي", ku: "کوردی" }, "en")).toBe("عربي");
    expect(localized({ ku: "کوردی" }, "en")).toBe("کوردی");
  });

  it("skips blank values and falls back", () => {
    expect(localized({ en: "  ", ar: "" }, "en", "x")).toBe("x");
    expect(localized(null, "en", "x")).toBe("x");
    expect(localized("plain", "en")).toBe("plain");
  });
});

describe("itemTitle / itemDescription", () => {
  it("uses the localized displayname, then the payload title, then the shortname", () => {
    expect(itemTitle({ shortname: "s", attributes: { displayname: { en: "Name" } } }, "en")).toBe("Name");
    expect(itemTitle({ shortname: "s", attributes: { payload: { body: { title: "Ticket" } } } }, "en")).toBe("Ticket");
    expect(itemTitle({ shortname: "s", attributes: { displayname: { en: "" } } }, "en")).toBe("s");
    expect(itemTitle({ shortname: "s" }, "en")).toBe("s");
  });

  it("description is '' when absent", () => {
    expect(itemDescription({ shortname: "s" }, "en")).toBe("");
    expect(itemDescription({ shortname: "s", attributes: { description: { ar: "وصف" } } }, "en")).toBe("وصف");
  });
});

describe("folderNameOf", () => {
  it("returns the last segment or null for the root", () => {
    expect(folderNameOf("/a/b")).toBe("b");
    expect(folderNameOf("a/")).toBe("a");
    expect(folderNameOf("/")).toBeNull();
    expect(folderNameOf("")).toBeNull();
    expect(folderNameOf(undefined)).toBeNull();
  });
});

describe("tagsOf", () => {
  it("accepts an array or a comma list and drops blanks and duplicates", () => {
    expect(tagsOf({ attributes: { tags: ["a", " b ", "", "a", 3] } })).toEqual(["a", "b"]);
    expect(tagsOf({ attributes: { tags: "x, y,,x" } })).toEqual(["x", "y"]);
    expect(tagsOf({ attributes: {} })).toEqual([]);
    expect(tagsOf({})).toEqual([]);
  });
});

describe("attachmentCounts", () => {
  const attachments = {
    comment: [{ resource_type: "comment" }, { resource_type: "comment" }],
    reaction: [{ resource_type: "reaction" }],
    media: [{ resource_type: "media" }],
    json: [{ resource_type: "json", attributes: { payload: { content_type: "image/png" } } }],
    other: "not an array",
  };

  it("counts by resource type across every group", () => {
    expect(attachmentCounts(attachments)).toEqual({ comments: 2, reactions: 1, media: 2 });
  });

  it("is zero for nothing", () => {
    expect(attachmentCounts(undefined)).toEqual({ comments: 0, reactions: 0, media: 0 });
    expect(attachmentCounts(null)).toEqual({ comments: 0, reactions: 0, media: 0 });
    expect(attachmentCounts({})).toEqual({ comments: 0, reactions: 0, media: 0 });
  });

  it("isHot needs a few reactions or comments", () => {
    expect(isHot({ comments: 0, reactions: 6, media: 0 })).toBe(true);
    expect(isHot({ comments: 3, reactions: 0, media: 0 })).toBe(true);
    expect(isHot({ comments: 2, reactions: 5, media: 9 })).toBe(false);
  });
});

describe("findUserReactionId", () => {
  const attachments = {
    reaction: [
      { resource_type: "reaction", shortname: "r1", attributes: { owner_shortname: "alice" } },
      { resource_type: "reaction", shortname: "r2", attributes: { owner_shortname: "bob" } },
    ],
    comment: [{ resource_type: "comment", shortname: "c1", attributes: { owner_shortname: "bob" } }],
  };

  it("finds the user's own reaction only", () => {
    expect(findUserReactionId(attachments, "bob")).toBe("r2");
    expect(findUserReactionId(attachments, "carol")).toBeNull();
    expect(findUserReactionId(attachments, undefined)).toBeNull();
    expect(findUserReactionId(undefined, "bob")).toBeNull();
  });
});

describe("previewText", () => {
  it("strips HTML and entities", () => {
    expect(previewText({ content_type: "html", body: "<p>Hello&nbsp;<b>world</b> &amp; <img alt=\"pic\" src=x></p>" })).toBe(
      "Hello world & [pic]",
    );
  });

  it("drops Markdown syntax", () => {
    expect(previewText({ content_type: "markdown", body: "# Title\n\n**bold** and [link](http://x) `code`\n- item" })).toBe(
      "Title bold and link code item",
    );
  });

  it("reads json bodies through content or key/value pairs", () => {
    expect(previewText({ content_type: "json", body: { title: "T", content: "<p>Body</p>" } })).toBe("Body");
    expect(previewText({ content_type: "json", body: { title: "T", a: 1, b: { c: 2 } } })).toBe('a: 1 · b: {"c":2}');
  });

  it("truncates at a word boundary with an ellipsis", () => {
    const long = Array.from({ length: 40 }, (_, i) => `word${i}`).join(" ");
    const out = previewText({ body: long }, 50);
    expect(out.length).toBeLessThanOrEqual(51);
    expect(out.endsWith("…")).toBe(true);
    // Every word kept is a whole word from the input: no "word1" cut to "wor".
    expect(out.slice(0, -1).split(" ").every((w) => /^word\d+$/.test(w))).toBe(true);
  });

  it("is '' for nothing", () => {
    expect(previewText(undefined)).toBe("");
    expect(previewText({ body: null })).toBe("");
    expect(previewText({ body: "   " })).toBe("");
  });
});
