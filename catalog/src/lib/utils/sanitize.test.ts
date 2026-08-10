// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { sanitizeHtml } from "./sanitize";

/**
 * sanitizeHtml sits in front of every `{@html ...}` sink whose content comes
 * from entry payloads, user input or rendered Markdown — i.e. the app's stored
 * XSS surface. DOMPurify does the actual filtering and is well tested upstream;
 * what is worth pinning here is that this wrapper always *reaches* it, and that
 * its two escape hatches (non-string input, no-DOM SSG context) behave as the
 * comment claims.
 */
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("sanitizeHtml", () => {
  describe("non-string input", () => {
    it("returns '' for null and undefined", () => {
      expect(sanitizeHtml(null)).toBe("");
      expect(sanitizeHtml(undefined)).toBe("");
    });

    // The signature admits Promise<string> only because marked() is typed
    // `string | Promise<string>`; async markdown is never enabled, so a
    // promise arriving here means something upstream changed.
    it("returns '' for a Promise rather than stringifying it", () => {
      expect(sanitizeHtml(Promise.resolve("<b>hi</b>"))).toBe("");
    });

    it("returns '' for values that are not strings at all", () => {
      expect(sanitizeHtml(42 as any)).toBe("");
      expect(sanitizeHtml({} as any)).toBe("");
    });
  });

  describe("in the browser", () => {
    it("passes benign markup through", () => {
      expect(sanitizeHtml("<p>hello <b>world</b></p>")).toBe(
        "<p>hello <b>world</b></p>",
      );
    });

    it("preserves plain text and the empty string", () => {
      expect(sanitizeHtml("just text")).toBe("just text");
      expect(sanitizeHtml("")).toBe("");
    });

    it("strips script elements", () => {
      const out = sanitizeHtml('<p>ok</p><script>alert(1)</script>');
      expect(out).not.toMatch(/<script/i);
      expect(out).toContain("<p>ok</p>");
    });

    it("strips inline event handlers", () => {
      const out = sanitizeHtml('<img src="x" onerror="alert(1)">');
      expect(out).not.toMatch(/onerror/i);
    });

    it("strips javascript: URLs", () => {
      const out = sanitizeHtml('<a href="javascript:alert(1)">click</a>');
      expect(out).not.toMatch(/javascript:/i);
    });

    it("strips iframes", () => {
      const out = sanitizeHtml('<iframe src="https://evil.test"></iframe>');
      expect(out).not.toMatch(/<iframe/i);
    });

    it("neutralises an svg onload payload", () => {
      const out = sanitizeHtml("<svg><script>alert(1)</script></svg>");
      expect(out).not.toMatch(/<script/i);
    });

    it("always returns a string", () => {
      expect(typeof sanitizeHtml("<p>x</p>")).toBe("string");
    });
  });

  describe("without a DOM (the SSG pre-render path)", () => {
    // spank/tossr pre-renders a few routes in Node, where DOMPurify cannot
    // run. Those pages carry no user payload and the browser re-sanitises on
    // hydration, so the input is returned unchanged by design. Asserted so the
    // passthrough stays a deliberate, documented choice.
    it("returns the input unchanged", () => {
      vi.stubGlobal("window", undefined);
      const dirty = '<p>ok</p><script>alert(1)</script>';
      expect(sanitizeHtml(dirty)).toBe(dirty);
    });

    it("still rejects non-string input", () => {
      vi.stubGlobal("window", undefined);
      expect(sanitizeHtml(null)).toBe("");
    });
  });
});
