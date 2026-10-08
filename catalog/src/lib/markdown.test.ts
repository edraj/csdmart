// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { escapeHtml, fenceLanguage, renderMarkdown, renderMarkdownInline } from "./markdown";

describe("renderMarkdown", () => {
  it("returns '' for a missing or non-string source", () => {
    expect(renderMarkdown(undefined)).toBe("");
    expect(renderMarkdown(null)).toBe("");
    expect(renderMarkdown("")).toBe("");
  });

  it("renders emphasis and prefixed heading ids", () => {
    const html = renderMarkdown("# Role-Based Access\n\n**bold** and _em_");
    expect(html).toContain('<h1 id="md-role-based-access">');
    expect(html).toContain("<strong>bold</strong>");
    expect(html).toContain("<em>em</em>");
  });

  it("strips scripts and event handlers", () => {
    const html = renderMarkdown('hi <script>alert(1)</script> <img src=x onerror="alert(1)">');
    expect(html).not.toContain("<script");
    expect(html).not.toContain("onerror");
  });

  it("renders a mermaid fence as a labelled source block", () => {
    const html = renderMarkdown("```mermaid\ngraph TD; A-->B;\n```", { diagramLabel: "Diagram <source>" });
    expect(html).toContain('<figure class="md-diagram" data-lang="mermaid">');
    expect(html).toContain('<code class="language-mermaid">graph TD; A--&gt;B;');
    expect(html).toContain("<figcaption>Diagram &lt;source&gt;</figcaption>");
  });

  it("leaves other fences to the default renderer", () => {
    const html = renderMarkdown("```js\nconst a = 1 < 2;\n```");
    expect(html).toContain('<pre><code class="language-js">const a = 1 &lt; 2;');
    expect(html).not.toContain("md-diagram");
  });

  it("does not grow between calls (one shared instance)", () => {
    const first = renderMarkdown("**a**");
    for (let i = 0; i < 20; i++) renderMarkdown("**a**");
    expect(renderMarkdown("**a**")).toBe(first);
  });
});

describe("renderMarkdownInline", () => {
  it("renders without a wrapping paragraph", () => {
    expect(renderMarkdownInline("**Role-Based Access Control (RBAC)**")).toBe(
      "<strong>Role-Based Access Control (RBAC)</strong>",
    );
    expect(renderMarkdownInline(null)).toBe("");
  });
});

describe("helpers", () => {
  it("fenceLanguage takes the first word, lower-cased", () => {
    expect(fenceLanguage("Mermaid title=x")).toBe("mermaid");
    expect(fenceLanguage(undefined)).toBe("");
    expect(fenceLanguage("  ")).toBe("");
  });

  it("escapeHtml escapes the four characters that matter", () => {
    expect(escapeHtml('<a href="x">&</a>')).toBe("&lt;a href=&quot;x&quot;&gt;&amp;&lt;/a&gt;");
  });
});
