import { describe, expect, it } from "vitest";
import { attachmentMarkdown, sanitizeLabel } from "./markdownInsert";

describe("attachmentMarkdown", () => {
  const url = "http://localhost:8000/managed/payload/media/space/sub/parent/shot.png";

  it("emits an inline image for image attachments", () => {
    expect(attachmentMarkdown("shot", url, "shot.png")).toBe(`![shot](${url})`);
  });

  it.each(["jpg", "jpeg", "png", "gif", "bmp", "webp", "svg"])(
    "treats .%s as an image",
    (ext) => {
      expect(attachmentMarkdown("a", url, `a.${ext}`)).toBe(`![a](${url})`);
    },
  );

  it("emits a link for a non-image, so it does not render as a broken image", () => {
    expect(attachmentMarkdown("report", url, "report.pdf")).toBe(`[report](${url})`);
  });

  it("emits a link when the filename has no extension at all", () => {
    expect(attachmentMarkdown("blob", url, "blob")).toBe(`[blob](${url})`);
  });

  it("is case-insensitive about the extension", () => {
    expect(attachmentMarkdown("a", url, "A.PNG")).toBe(`![a](${url})`);
  });
});

describe("sanitizeLabel", () => {
  it("escapes brackets so a label cannot terminate the link text early", () => {
    expect(sanitizeLabel("front]back")).toBe("front\\]back");
    expect(sanitizeLabel("a[b]c")).toBe("a\\[b\\]c");
  });

  it("passes an ordinary label through untouched", () => {
    expect(sanitizeLabel("site_photo_01")).toBe("site_photo_01");
  });

  it("tolerates an empty or missing label", () => {
    expect(sanitizeLabel("")).toBe("");
    expect(sanitizeLabel(undefined as any)).toBe("");
  });
});
