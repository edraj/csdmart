// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { renderMarkdown } from "./markdown";

function parse(html: string): HTMLElement {
    const root = document.createElement("div");
    root.innerHTML = html;
    return root;
}

describe("renderMarkdown", () => {
    it("turns a mermaid fence into a diagram placeholder that survives sanitising", async () => {
        const source = '```mermaid\ngraph TD\n  A["Space: <b>x</b> & y"] --> B\n```';
        const root = parse(await renderMarkdown(source));
        const figure = root.querySelector<HTMLElement>('figure.md-diagram[data-diagram="mermaid"]');
        expect(figure).not.toBeNull();
        expect(figure!.querySelector("code")!.textContent).toBe('graph TD\n  A["Space: <b>x</b> & y"] --> B');
        // The label's markup stays text until mermaid draws it.
        expect(figure!.querySelector("b")).toBeNull();
    });

    it("matches the language case-insensitively and ignores fence attributes", async () => {
        const root = parse(await renderMarkdown("```Mermaid title=x\ngraph LR\n  A --> B\n```"));
        expect(root.querySelector('figure[data-diagram="mermaid"]')).not.toBeNull();
    });

    it("leaves every other fence an ordinary code block", async () => {
        const root = parse(await renderMarkdown("```js\nconst a = 1 < 2;\n```\n\n```\nplain\n```"));
        expect(root.querySelector("figure")).toBeNull();
        expect(root.querySelectorAll("pre > code")).toHaveLength(2);
        expect(root.querySelector("pre > code.language-js")!.textContent).toBe("const a = 1 < 2;\n");
    });
});
