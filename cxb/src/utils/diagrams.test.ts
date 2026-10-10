// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";

const engine = vi.hoisted(() => ({
    initialize: vi.fn(),
    parse: vi.fn(async (source: string) => {
        if (source.includes("oops")) throw new Error("Parse error on line 2");
        return { diagramType: "flowchart" };
    }),
    render: vi.fn(async (id: string, source: string) => ({ svg: `<svg id="${id}"><text>${source.length}</text></svg>` })),
}));
vi.mock("mermaid", () => ({ default: engine }));

import { renderDiagrams } from "@shared/diagrams";

const options = { dark: false, errorLabel: "This diagram could not be drawn" };

function article(...sources: string[]): HTMLElement {
    const root = document.createElement("article");
    for (const source of sources) {
        const figure = document.createElement("figure");
        figure.className = "md-diagram";
        figure.dataset.diagram = "mermaid";
        figure.innerHTML = "<pre><code></code></pre>";
        figure.querySelector("code")!.textContent = source;
        root.append(figure);
    }
    document.body.append(root);
    return root;
}

describe("renderDiagrams", () => {
    beforeEach(() => {
        document.body.replaceChildren();
        vi.clearAllMocks();
    });

    it("draws inside the figure, keeping the figure node {@html} owns", async () => {
        const root = article("graph TD\n  A --> B");
        const figure = root.firstElementChild!;
        await renderDiagrams(root, options);
        expect(root.firstElementChild).toBe(figure);
        expect(figure.querySelector("svg")).not.toBeNull();
        expect(figure.querySelector("pre")).toBeNull();
        expect((figure as HTMLElement).dataset.rendered).toBe("light");
        expect(engine.initialize).toHaveBeenCalledWith(expect.objectContaining({ securityLevel: "strict", theme: "default" }));
    });

    it("draws an already drawn figure again from its original source when the theme changes", async () => {
        const root = article("graph TD\n  A --> B");
        await renderDiagrams(root, options);
        await renderDiagrams(root, { ...options, dark: true });
        expect(engine.initialize).toHaveBeenLastCalledWith(expect.objectContaining({ theme: "dark" }));
        expect(engine.render).toHaveBeenLastCalledWith(expect.any(String), "graph TD\n  A --> B");
    });

    it("shows the source under an error caption when the diagram does not parse, and draws the rest", async () => {
        const root = article("graph TD\n  oops -->", "graph LR\n  A --> B");
        await renderDiagrams(root, options);
        const [bad, good] = Array.from(root.children);
        expect(bad.querySelector("figcaption")!.textContent).toBe("This diagram could not be drawn: Parse error on line 2");
        expect(bad.querySelector("code")!.textContent).toBe("graph TD\n  oops -->");
        expect(bad.querySelector("svg")).toBeNull();
        expect(good.querySelector("svg")).not.toBeNull();
        expect(engine.render).toHaveBeenCalledTimes(1);
    });

    it("leaves a figure alone that left the page while mermaid was drawing", async () => {
        const root = article("graph TD\n  A --> B");
        const figure = root.firstElementChild!;
        engine.render.mockImplementationOnce(async (id: string) => {
            figure.remove();
            return { svg: `<svg id="${id}"></svg>` };
        });
        await renderDiagrams(root, options);
        expect(figure.querySelector("svg")).toBeNull();
    });

    it("skips a figure already drawn in the current theme", async () => {
        const root = article("graph TD\n  A --> B");
        await renderDiagrams(root, options);
        await renderDiagrams(root, options);
        expect(engine.render).toHaveBeenCalledTimes(1);
    });

    it("does not load mermaid for markdown without a diagram", async () => {
        const root = document.createElement("article");
        root.innerHTML = "<pre><code>graph TD</code></pre>";
        await renderDiagrams(root, options);
        expect(engine.initialize).not.toHaveBeenCalled();
    });
});
