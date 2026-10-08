<script lang="ts">
    import { onMount, onDestroy } from "svelte";
    import type { Editor, EditorRange } from "typewriter-editor";
    import { Input, Label } from "flowbite-svelte";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { _ } from "@/i18n";

    // The rich-text editor. `typewriter-editor` (~80 kB) is imported when the
    // editor mounts, not when the page that might show it loads; the toolbar
    // is ordinary markup so every button has a name and follows the theme.
    let {
        uid = "",
        content = $bindable(""),
    }: {
        uid?: string;
        content: string;
    } = $props();

    let maindiv: HTMLDivElement;
    let editor: Editor | null = $state(null);
    let loading = $state(true);
    let loadError = $state<unknown>(null);

    type Tool = { key: string; glyph: string; run: () => void };
    type ToolGroup = { key: string; tools: Tool[] };

    const groups: ToolGroup[] = [
        {
            key: "text",
            tools: [
                { key: "editor_bold", glyph: "B", run: () => editor?.formatText("bold") },
                { key: "editor_italic", glyph: "I", run: () => editor?.formatText("italic") },
                { key: "editor_underline", glyph: "U", run: () => editor?.formatText("underline") },
                { key: "editor_strike", glyph: "S", run: () => editor?.formatText("strike") },
                { key: "editor_superscript", glyph: "x²", run: () => editor?.formatText("superscript") },
                { key: "editor_subscript", glyph: "x₂", run: () => editor?.formatText("subscript") },
                { key: "editor_clear_format", glyph: "✗", run: () => editor?.removeFormat() },
            ],
        },
        {
            key: "line",
            tools: [
                { key: "editor_heading_1", glyph: "H1", run: () => editor?.formatLine({ header: 1 }) },
                { key: "editor_heading_2", glyph: "H2", run: () => editor?.formatLine({ header: 2 }) },
                { key: "editor_paragraph", glyph: "¶", run: () => editor?.formatLine("paragraph") },
                { key: "editor_blockquote", glyph: "“”", run: () => editor?.formatLine("blockquote") },
                { key: "editor_ordered_list", glyph: "1.", run: () => editor?.formatLine({ list: "ordered" }) },
                { key: "editor_bullet_list", glyph: "•", run: () => editor?.formatLine({ list: "bullet" }) },
                { key: "editor_horizontal_rule", glyph: "—", run: () => editor?.formatLine("hr") },
            ],
        },
        {
            key: "align",
            tools: [
                { key: "editor_align_left", glyph: "⇤", run: () => editor?.formatLine("align-left") },
                { key: "editor_align_center", glyph: "↔", run: () => editor?.formatLine("align-center") },
                { key: "editor_align_right", glyph: "⇥", run: () => editor?.formatLine("align-right") },
                { key: "editor_justify", glyph: "☰", run: () => editor?.formatLine("align-justify") },
            ],
        },
        {
            key: "insert",
            tools: [
                { key: "editor_link", glyph: "\u{1F517}", run: () => askUrl("link") },
                { key: "editor_image", glyph: "\u{1F5BC}", run: () => askUrl("image") },
            ],
        },
        {
            key: "history",
            tools: [
                { key: "editor_undo", glyph: "↶", run: () => editor?.modules.history?.undo() },
                { key: "editor_redo", glyph: "↷", run: () => editor?.modules.history?.redo() },
            ],
        },
        {
            key: "direction",
            tools: [
                {
                    key: "editor_ltr",
                    glyph: "LTR",
                    run: () => {
                        maindiv.dir = "ltr";
                        editor?.formatLine({ direction: "ltr" });
                    },
                },
                {
                    key: "editor_rtl",
                    glyph: "RTL",
                    run: () => {
                        maindiv.dir = "rtl";
                        editor?.formatLine({ direction: "rtl" });
                    },
                },
            ],
        },
    ];

    // ── Link / image URL: a dialog instead of window.prompt ────────────────
    let urlDialogOpen = $state(false);
    let urlKind = $state<"link" | "image">("link");
    let urlValue = $state("");
    // The dialog takes focus, so the selection is captured before it opens.
    let savedSelection: EditorRange | null = null;

    function askUrl(kind: "link" | "image") {
        savedSelection = editor?.doc.selection ?? null;
        urlKind = kind;
        urlValue = "";
        urlDialogOpen = true;
    }

    function applyUrl() {
        const value = urlValue.trim();
        urlDialogOpen = false;
        if (!value || !editor) return;
        if (urlKind === "link") {
            editor.formatText({ link: value }, savedSelection);
        } else {
            editor.insert({ image: value }, undefined, savedSelection);
        }
    }

    onMount(async () => {
        try {
            const { Editor, format, h } = await import("typewriter-editor");
            type Fmt = Parameters<typeof format>[0];

            const textFormat = (name: string, tag: string, selector: string, extra: Partial<Fmt> = {}): Fmt => ({
                name,
                selector,
                commands: (ed: Editor) => () => ed.toggleTextFormat({ [name]: true }),
                render: (_attributes, children) => h(tag, null, children),
                ...extra,
            });

            const underline = format(
                textFormat("underline", "u", "u", {
                    styleSelector: '[style*="text-decoration:underline"], [style*="text-decoration: underline"]',
                    shortcuts: "Mod+U",
                }),
            );
            const strike = format(
                textFormat("strike", "s", "strike, s", {
                    styleSelector: '[style*="text-decoration:line-through"], [style*="text-decoration: line-through"]',
                    shortcuts: "Mod+Shift+X",
                }),
            );
            const superscript = format(textFormat("superscript", "sup", "sup"));
            const subscript = format(textFormat("subscript", "sub", "sub"));

            const alignFormat = (align: string): Fmt => ({
                name: `align-${align}`,
                selector: `[style*="text-align:${align}"], [style*="text-align: ${align}"]`,
                commands: (ed: Editor) => () => ed.formatLine({ align }),
                render: (_attributes, children) => h("div", { style: `text-align: ${align}` }, children),
            });

            editor = new Editor({
                root: maindiv,
                html: content,
                types: {
                    lines: [
                        "paragraph",
                        "header",
                        "list",
                        "blockquote",
                        "code-block",
                        "hr",
                        format(alignFormat("left")),
                        format(alignFormat("center")),
                        format(alignFormat("right")),
                        format(alignFormat("justify")),
                    ],
                    formats: ["bold", "italic", underline, strike, superscript, subscript, "code", "link", "clear"],
                    embeds: ["image", "br"],
                },
            });

            editor.on("change", () => {
                content = editor?.getHTML() ?? content;
            });
        } catch (error) {
            loadError = error;
        } finally {
            loading = false;
        }
    });

    onDestroy(() => {
        editor?.destroy();
        editor = null;
    });

    $effect(() => {
        if (editor) {
            const currentHtml = editor.getHTML();
            if (content !== currentHtml) {
                editor.setHTML(content);
            }
        }
    });
</script>

<div class="rounded-card border border-border bg-surface-2 shadow-card">
    <div class="flex flex-wrap gap-2 p-2 border-b border-border bg-surface rounded-t-card" role="toolbar" aria-label={$_("editor_toolbar")}>
        {#each groups as group (group.key)}
            <div class="flex flex-wrap gap-0.5 rounded-control border border-border bg-surface-2 p-0.5" role="group">
                {#each group.tools as tool (tool.key)}
                    <button
                        type="button"
                        class="inline-flex items-center justify-center min-w-8 h-8 px-1.5 rounded-control text-xs font-medium text-text-muted hover:text-text hover:bg-surface-3 active:bg-surface-3 cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed"
                        aria-label={$_(tool.key)}
                        title={$_(tool.key)}
                        disabled={!editor}
                        onclick={tool.run}
                    >
                        <span aria-hidden="true">{tool.glyph}</span>
                    </button>
                {/each}
            </div>
        {/each}
    </div>

    {#if loading}
        <div class="p-4"><LoadingState variant="skeleton" rows={5} /></div>
    {:else if loadError}
        <p class="p-4 text-sm text-danger">{$_("editor_load_failed")}</p>
    {/if}
    <article class="prose dark:prose-invert max-w-none p-4" class:hidden={loading || !!loadError}>
        <div class="editor-container" bind:this={maindiv} id="htmleditor-{uid}"></div>
    </article>
</div>

<ConfirmDialog
    bind:open={urlDialogOpen}
    variant="primary"
    title={urlKind === "link" ? $_("editor_link") : $_("editor_image")}
    confirmLabel={$_("insert")}
    onConfirm={applyUrl}
>
    <Label for="htmleditor-{uid}-url" class="mb-1.5">{$_("url")}</Label>
    <Input id="htmleditor-{uid}-url" type="url" bind:value={urlValue} placeholder="https://" />
</ConfirmDialog>

<style>
    .editor-container {
        min-height: 200px;
        outline: none;
    }

    :global(.editor-container blockquote) {
        border-inline-start: 3px solid var(--color-border-strong);
        padding-inline-start: 1rem;
        margin-inline-start: 0;
        font-style: italic;
    }
</style>
