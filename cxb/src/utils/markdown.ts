/**
 * Markdown → sanitised HTML, with `marked` and DOMPurify loaded on first use.
 *
 * One `Marked` instance for the whole session: the old components called
 * `marked.use(mangle()); marked.use(gfmHeadingId())` on the global parser in
 * every component instance, so the extension chain grew for as long as the tab
 * was open. The libraries are dynamically imported so a list route whose
 * chunk merely links an editor does not download them.
 */
type Renderer = (source: string) => Promise<string>;

let renderer: Promise<Renderer> | null = null;

async function createRenderer(): Promise<Renderer> {
    const [{ Marked }, { mangle }, { gfmHeadingId }, { default: DOMPurify }] = await Promise.all([
        import("marked"),
        import("marked-mangle"),
        import("marked-gfm-heading-id"),
        import("dompurify"),
    ]);
    const marked = new Marked(mangle(), gfmHeadingId({ prefix: "md-" }));
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
