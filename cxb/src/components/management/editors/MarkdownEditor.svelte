<script lang="ts">
    import { TabItem, Tabs } from "flowbite-svelte";
    import MarkdownView from "@/components/management/renderers/MarkdownView.svelte";
    import { _ } from "@/i18n";

    // Markdown source with a preview tab. `marked` and DOMPurify are loaded by
    // renderMarkdown() the first time the preview is shown, and configured once
    // for the whole session there (not per editor instance); mermaid only when
    // the preview has a diagram to draw.
    let {
        content = $bindable(""),
    }: {
        content: string;
    } = $props();

    if (typeof content !== "string") {
        content = "";
    }

    let textarea: HTMLTextAreaElement | undefined = $state();
    let start = 0,
        end = 0;
    function handleSelect() {
        if (!textarea) return;
        start = textarea.selectionStart;
        end = textarea.selectionEnd;
    }

    const listViewInsert =
        "{% ListView \n" +
        '   type="subpath"\n' +
        '   space_name="" \n' +
        '   subpath="/" \n' +
        "   is_clickable=false %}\n" +
        "{% /ListView %}\n";
    const tableInsert = `| Header 1 | Header 2 |
|----------|----------|
|  Cell1   |  Cell2   |`;

    function handleKeyDown(event: KeyboardEvent) {
        if (event.ctrlKey && ["b", "i", "t"].includes(event.key)) {
            event.preventDefault();
            switch (event.key) {
                case "b":
                    handleFormatting("**");
                    break;
                case "i":
                    handleFormatting("_");
                    break;
                case "t":
                    handleFormatting("~~");
                    break;
            }
        }
    }

    function handleFormatting(format: string, isWrap = true, isPerLine = false) {
        if (!textarea) return;
        if (isWrap && start === 0 && end === 0) {
            return;
        }
        if (isWrap) {
            textarea.value =
                textarea.value.substring(0, start) +
                format +
                textarea.value.substring(start, end) +
                format +
                textarea.value.substring(end);
        } else {
            start = textarea.selectionStart;
            end = textarea.selectionEnd;
            if (isPerLine) {
                const lines = textarea.value.split("\n");
                const lineStart = textarea.value.substring(0, start).split("\n").length - 1;
                let lineEnd = textarea.value.substring(0, end).split("\n").length - 1;

                if (textarea.value[end] === "\n") {
                    lineEnd--;
                }

                for (let i = lineStart; i <= lineEnd; i++) {
                    lines[i] = `${format} ` + lines[i];
                }

                textarea.value = lines.join("\n");
            } else {
                const lineStart = textarea.value.lastIndexOf("\n", start - 1) + 1;
                let lineEnd = textarea.value.indexOf("\n", end);
                if (lineEnd === -1) {
                    lineEnd = textarea.value.length;
                }
                textarea.value =
                    textarea.value.substring(0, lineStart) +
                    `${format} ` +
                    textarea.value.substring(lineStart, lineEnd) +
                    textarea.value.substring(lineEnd);
            }
        }

        start = 0;
        end = 0;
        content = textarea.value;
    }

    type Tool = { key: string; glyph: string; run: () => void; strong?: boolean };
    const tools: Tool[] = [
        { key: "editor_bold", glyph: "B", strong: true, run: () => handleFormatting("**") },
        { key: "editor_italic", glyph: "I", run: () => handleFormatting("_") },
        { key: "editor_strike", glyph: "S", run: () => handleFormatting("~~") },
        { key: "editor_bullet_list", glyph: "•", run: () => handleFormatting("*", false, true) },
        { key: "editor_ordered_list", glyph: "1.", run: () => handleFormatting("1.", false, true) },
        { key: "editor_heading_1", glyph: "H1", run: () => handleFormatting("#", false) },
        { key: "editor_heading_2", glyph: "H2", run: () => handleFormatting("##", false) },
        { key: "editor_heading_3", glyph: "H3", run: () => handleFormatting("###", false) },
        { key: "editor_table", glyph: "⊞", run: () => handleFormatting(tableInsert, false) },
        { key: "editor_list_view", glyph: "☰", run: () => handleFormatting(listViewInsert, false) },
    ];

    const tabActive = "px-3 py-2 text-sm font-medium border-b-2 border-primary text-primary bg-transparent rounded-none";
    const tabInactive =
        "px-3 py-2 text-sm font-medium border-b-2 border-transparent text-text-muted hover:text-text hover:border-border-strong bg-transparent rounded-none";
</script>

<div class="rounded-card border border-border bg-surface-2 shadow-card">
    <div class="flex flex-wrap gap-0.5 p-2 border-b border-border bg-surface rounded-t-card" role="toolbar" aria-label={$_("editor_toolbar")}>
        {#each tools as tool (tool.key)}
            <button
                type="button"
                class="inline-flex items-center justify-center min-w-8 h-8 px-1.5 rounded-control text-xs text-text-muted hover:text-text hover:bg-surface-3 cursor-pointer {tool.strong ? 'font-bold' : 'font-medium'}"
                aria-label={$_(tool.key)}
                title={$_(tool.key)}
                onclick={tool.run}
            >
                <span aria-hidden="true">{tool.glyph}</span>
            </button>
        {/each}
    </div>

    <Tabs tabStyle="underline" divider={false} class="px-2 border-b border-border space-x-0 rtl:space-x-reverse" classes={{ content: "p-3 bg-transparent dark:bg-transparent rounded-none mt-0" }}>
        <TabItem open title={$_("editor")} activeClass={tabActive} inactiveClass={tabInactive}>
            <label for="markdown-source" class="sr-only">{$_("editor")}</label>
            <textarea
                id="markdown-source"
                bind:this={textarea}
                onselect={handleSelect}
                onkeydown={handleKeyDown}
                rows="22"
                dir="auto"
                class="w-full font-mono text-sm bg-surface-2 text-text border border-border rounded-control p-2.5 focus:ring-primary focus:border-primary"
                bind:value={content}
            ></textarea>
        </TabItem>
        <TabItem title={$_("preview")} activeClass={tabActive} inactiveClass={tabInactive}>
            <MarkdownView source={content} />
        </TabItem>
    </Tabs>
</div>
