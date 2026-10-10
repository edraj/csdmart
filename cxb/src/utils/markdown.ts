/**
 * Markdown → sanitised HTML, with `marked` and DOMPurify loaded on first use.
 *
 * One `Marked` instance for the whole session: the old components called
 * `marked.use(mangle()); marked.use(gfmHeadingId())` on the global parser in
 * every component instance, so the extension chain grew for as long as the tab
 * was open. The libraries are dynamically imported so a list route whose
 * chunk merely links an editor does not download them.
 *
 * A ```mermaid fence becomes a placeholder figure holding its escaped source;
 * renderDiagrams() (utils/diagrams.ts) draws it once the HTML is in the page.
 */
type Renderer = (source: string) => Promise<string>;

let renderer: Promise<Renderer> | null = null;

function escapeHtml(text: string): string {
    return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

async function createRenderer(): Promise<Renderer> {
    const [{ Marked }, { mangle }, { gfmHeadingId }, { default: DOMPurify }] = await Promise.all([
        import("marked"),
        import("marked-mangle"),
        import("marked-gfm-heading-id"),
        import("dompurify"),
    ]);
    const marked = new Marked(mangle(), gfmHeadingId({ prefix: "md-" }), {
        renderer: {
            code({ text, lang }) {
                if (lang?.trim().split(/\s+/)[0].toLowerCase() !== "mermaid") return false;
                return `<figure class="md-diagram" data-diagram="mermaid"><pre><code>${escapeHtml(text)}</code></pre></figure>\n`;
            },
        },
    });
    return async (source: string) => {
        const html = await marked.parse(source ?? "");
        // marked does NOT strip HTML and the source is server-supplied content.
        return DOMPurify.sanitize(html);
    };
}

export function renderMarkdown(source: string): Promise<string> {
    renderer ??= createRenderer();
    return renderer.then((render) => render(typeof source === "string" ? source : ""));
}
