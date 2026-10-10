/**
 * Draws the diagrams in rendered markdown.
 *
 * renderMarkdown() turns a ```mermaid fence into
 * `<figure class="md-diagram" data-diagram="mermaid"><pre><code>…</code></pre></figure>`
 * and this replaces the figure's contents with the SVG mermaid draws. Mermaid
 * is bundled, not taken from a CDN, because the CSP allows scripts from 'self'
 * only; it is imported on first use, so a page without a diagram never
 * downloads it.
 *
 * Only the figure's children are replaced, never the figure itself: the figure
 * is one of the nodes Svelte's {@html} inserted, and it removes exactly those
 * nodes when the markdown changes.
 */
import type { Mermaid } from "mermaid";

export interface DiagramOptions {
    dark: boolean;
    /** Put before mermaid's message when a diagram cannot be drawn. */
    errorLabel: string;
}

let mermaid: Promise<Mermaid> | null = null;
let sequence = 0;
// One pass at a time: initialize() sets mermaid's global config (the theme),
// so two overlapping passes could draw with each other's theme.
let queue: Promise<void> = Promise.resolve();
// The fence source of each figure, kept once the figure shows an SVG so a
// theme change can draw it again.
const sources = new WeakMap<Element, string>();

export function renderDiagrams(root: ParentNode, options: DiagramOptions): Promise<void> {
    const pass = queue.then(() => draw(root, options));
    queue = pass.catch(() => undefined);
    return pass;
}

async function draw(root: ParentNode, { dark, errorLabel }: DiagramOptions): Promise<void> {
    const figures = Array.from(root.querySelectorAll<HTMLElement>('figure[data-diagram="mermaid"]'));
    if (figures.length === 0) return;

    let engine: Mermaid;
    try {
        engine = await (mermaid ??= import("mermaid").then((module) => module.default));
    } catch (error) {
        // A chunk that failed to load is fetched again by the next pass.
        mermaid = null;
        for (const figure of figures) showSource(figure, sourceOf(figure), errorLabel, error);
        return;
    }
    engine.initialize({ startOnLoad: false, securityLevel: "strict", theme: dark ? "dark" : "default" });

    for (const figure of figures) {
        const source = sourceOf(figure);
        const id = `md-diagram-${++sequence}`;
        try {
            // parse() first: render() of an invalid diagram leaves mermaid's
            // "Syntax error in text" SVG at the end of <body>.
            await engine.parse(source);
            const { svg } = await engine.render(id, source);
            if (!figure.isConnected) continue;
            figure.innerHTML = svg;
            figure.dataset.rendered = "";
        } catch (error) {
            if (figure.isConnected) showSource(figure, source, errorLabel, error);
        } finally {
            // render() draws in a scratch element appended to <body>.
            document.getElementById(`d${id}`)?.remove();
        }
    }
}

function sourceOf(figure: HTMLElement): string {
    let source = sources.get(figure);
    if (source === undefined) {
        source = figure.querySelector("code")?.textContent ?? "";
        sources.set(figure, source);
    }
    return source;
}

function showSource(figure: HTMLElement, source: string, label: string, error: unknown): void {
    const caption = document.createElement("figcaption");
    caption.className = "md-diagram-error";
    caption.textContent = `${label}: ${error instanceof Error ? error.message : String(error)}`;
    const code = document.createElement("code");
    code.textContent = source;
    const pre = document.createElement("pre");
    pre.append(code);
    figure.replaceChildren(caption, pre);
    delete figure.dataset.rendered;
}
