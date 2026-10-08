<script lang="ts">
  import { Dmart } from "@edraj/tsdmart";
  import { onMount } from "svelte";
  import { getFileExtension } from "@shared/file-extension";
  import { _ } from "@/i18n";
  import Modal from "@/components/Modal.svelte";

  let {
    uid = "",
    content = $bindable(""),
    isEditMode = false,
    attachments,
    space_name,
    subpath,
    parent_shortname,
    changed = () => {},
  } = $props();

  /**
   * Syncs the current editor HTML into the bound `content` prop. Callers that
   * need to read the most up-to-date value right before submit (without
   * relying on the `change` event having already fired) can invoke this.
   */
  export function flush(): string {
    if (editor && typeof editor.getHTML === "function") {
      content = editor.getHTML();
    }
    return content ?? "";
  }

  let showAttachments = $state(false);
  // The link/image toolbar buttons used to call prompt(); they open this
  // small dialog instead (review #34).
  let urlDialog = $state<"link" | "image" | null>(null);
  let urlValue = $state("");
  const instanceId = $props.id();
  const urlFormId = `htmleditor-url-${instanceId}`;
  let maindiv: HTMLDivElement;
  let editor: any;

  let format: any;
  let h: any;
  let Editor: any;

  let underline, strike, superscript, subscript;
  let alignLeft, alignCenter, alignRight, alignJustify;

  onMount(async () => {
    const mod = await import("typewriter-editor");
    Editor = mod.Editor;
    h = mod.h;
    format = mod.format;

    underline = format({
      name: "underline",
      selector: "u",
      styleSelector:
        '[style*="text-decoration:underline"], [style*="text-decoration: underline"]',
      commands: (editor: any) => () => editor.toggleTextFormat({ underline: true }),
      shortcuts: "Mod+U",
      render: (attributes: any, children: any) => h("u", null, children),
    });

    strike = format({
      name: "strike",
      selector: "strike, s",
      styleSelector:
        '[style*="text-decoration:line-through"], [style*="text-decoration: line-through"]',
      commands: (editor: any) => () => editor.toggleTextFormat({ strike: true }),
      shortcuts: "Mod+Shift+X",
      render: (attributes: any, children: any) => h("s", null, children),
    });

    superscript = format({
      name: "superscript",
      selector: "sup",
      commands: (editor: any) => () =>
        editor.toggleTextFormat({ superscript: true }),
      render: (attributes: any, children: any) => h("sup", null, children),
    });

    subscript = format({
      name: "subscript",
      selector: "sub",
      commands: (editor: any) => () => editor.toggleTextFormat({ subscript: true }),
      render: (attributes: any, children: any) => h("sub", null, children),
    });

    alignLeft = format({
      name: "align-left",
      selector: '[style*="text-align:left"], [style*="text-align: left"]',
      commands: (editor: any) => () => editor.formatLine({ align: "left" }),
      render: (attributes: any, children: any) =>
        h("div", { style: "text-align: left" }, children),
    });

    alignCenter = format({
      name: "align-center",
      selector: '[style*="text-align:center"], [style*="text-align: center"]',
      commands: (editor: any) => () => editor.formatLine({ align: "center" }),
      render: (attributes: any, children: any) =>
        h("div", { style: "text-align: center" }, children),
    });

    alignRight = format({
      name: "align-right",
      selector: '[style*="text-align:right"], [style*="text-align: right"]',
      commands: (editor: any) => () => editor.formatLine({ align: "right" }),
      render: (attributes: any, children: any) =>
        h("div", { style: "text-align: right" }, children),
    });

    alignJustify = format({
      name: "align-justify",
      selector: '[style*="text-align:justify"], [style*="text-align: justify"]',
      commands: (editor: any) => () => editor.formatLine({ align: "justify" }),
      render: (attributes: any, children: any) =>
        h("div", { style: "text-align: justify" }, children),
    });

    editor = new Editor({
      root: maindiv,
      html: content || "",
      types: {
        lines: [
          "paragraph",
          "header",
          "list",
          "blockquote",
          "code-block",
          "hr",
          alignLeft,
          alignCenter,
          alignRight,
          alignJustify,
        ],
        formats: [
          "bold",
          "italic",
          underline,
          strike,
          superscript,
          subscript,
          "code",
          "link",
          "clear",
        ],
        embeds: ["image", "br"],
      },
    });

    editor.on("change", () => {
      content = editor.getHTML();
      changed();
    });

    setupToolbar();
  });

  function setupToolbar() {
    const toolbar = document.createElement("div");
    toolbar.id = `toolbar-${uid}`;
    toolbar.className = "editor-toolbar";

    const textFormatGroup = document.createElement("div");
    textFormatGroup.className = "toolbar-group";

    const lineFormatGroup = document.createElement("div");
    lineFormatGroup.className = "toolbar-group";

    const alignmentGroup = document.createElement("div");
    alignmentGroup.className = "toolbar-group";

    const insertGroup = document.createElement("div");
    insertGroup.className = "toolbar-group";

    const historyGroup = document.createElement("div");
    historyGroup.className = "toolbar-group";

    const directionGroup = document.createElement("div");
    directionGroup.className = "toolbar-group";

    const attachmentsGroup = document.createElement("div");
    attachmentsGroup.className = "toolbar-group";

    addToolbarButton(textFormatGroup, $_("html_editor.toolbar.bold"), "B", () =>
      editor.formatText("bold"),
    );
    addToolbarButton(textFormatGroup, $_("html_editor.toolbar.italic"), "I", () =>
      editor.formatText("italic"),
    );
    addToolbarButton(textFormatGroup, $_("html_editor.toolbar.underline"), "U", () =>
      editor.formatText("underline"),
    );
    addToolbarButton(textFormatGroup, $_("html_editor.toolbar.strike"), "S", () =>
      editor.formatText("strike"),
    );
    addToolbarButton(textFormatGroup, $_("html_editor.toolbar.superscript"), "x²", () =>
      editor.formatText("superscript"),
    );
    addToolbarButton(textFormatGroup, $_("html_editor.toolbar.subscript"), "x₂", () =>
      editor.formatText("subscript"),
    );
    addToolbarButton(textFormatGroup, $_("html_editor.toolbar.remove_format"), "X", () =>
      editor.removeFormat(),
    );

    addToolbarButton(lineFormatGroup, $_("html_editor.toolbar.heading_1"), "H1", () =>
      editor.formatLine({ header: 1 }),
    );
    addToolbarButton(lineFormatGroup, $_("html_editor.toolbar.heading_2"), "H2", () =>
      editor.formatLine({ header: 2 }),
    );
    addToolbarButton(lineFormatGroup, $_("html_editor.toolbar.paragraph"), "¶", () =>
      editor.formatLine("paragraph"),
    );
    addToolbarButton(lineFormatGroup, $_("html_editor.toolbar.blockquote"), '""', () =>
      editor.formatLine("blockquote"),
    );
    addToolbarButton(lineFormatGroup, $_("html_editor.toolbar.ordered_list"), "1.", () =>
      editor.formatLine({ list: "ordered" }),
    );
    addToolbarButton(lineFormatGroup, $_("html_editor.toolbar.unordered_list"), "•", () =>
      editor.formatLine({ list: "bullet" }),
    );
    addToolbarButton(lineFormatGroup, $_("html_editor.toolbar.horizontal_rule"), "—", () =>
      editor.formatLine("hr"),
    );

    addToolbarButton(alignmentGroup, $_("html_editor.toolbar.align_left"), "↤", () =>
      editor.formatLine("align-left"),
    );
    addToolbarButton(alignmentGroup, $_("html_editor.toolbar.align_center"), "↔", () =>
      editor.formatLine("align-center"),
    );
    addToolbarButton(alignmentGroup, $_("html_editor.toolbar.align_right"), "↦", () =>
      editor.formatLine("align-right"),
    );
    addToolbarButton(alignmentGroup, $_("html_editor.toolbar.justify"), "☰", () =>
      editor.formatLine("align-justify"),
    );

    addToolbarButton(insertGroup, $_("html_editor.toolbar.link"), "🔗", () =>
      openUrlDialog("link"),
    );

    addToolbarButton(insertGroup, $_("html_editor.toolbar.image"), "🖼", () =>
      openUrlDialog("image"),
    );

    if (isEditMode && attachments?.media?.length > 0) {
      addToolbarButton(attachmentsGroup, $_("html_editor.toolbar.attachments"), "📎", (event: any) => {
        event.preventDefault();
        event.stopPropagation();
        showAttachments = true;
      });
    }

    addToolbarButton(historyGroup, $_("html_editor.toolbar.undo"), "↶", () =>
      editor.modules.history.undo(),
    );
    addToolbarButton(historyGroup, $_("html_editor.toolbar.redo"), "↷", () =>
      editor.modules.history.redo(),
    );

    addToolbarButton(directionGroup, $_("html_editor.toolbar.ltr"), "LTR", () => {
      maindiv.dir = "ltr";
      editor.formatLine({ direction: "ltr" });
    });
    addToolbarButton(directionGroup, $_("html_editor.toolbar.rtl"), "RTL", () => {
      maindiv.dir = "rtl";
      editor.formatLine({ direction: "rtl" });
    });

    toolbar.appendChild(textFormatGroup);
    toolbar.appendChild(lineFormatGroup);
    toolbar.appendChild(alignmentGroup);
    toolbar.appendChild(insertGroup);
    if (isEditMode && attachments?.media?.length > 0) {
      toolbar.appendChild(attachmentsGroup);
    }
    toolbar.appendChild(historyGroup);
    toolbar.appendChild(directionGroup);

    maindiv.parentNode!.insertBefore(toolbar, maindiv);
  }

  function addToolbarButton(toolbar: any, title: any, icon: any, action: any) {
    const button = document.createElement("button");
    button.type = "button";
    button.title = title;
    button.className = "toolbar-button";
    button.textContent = icon;

    // Prevent the editor from losing focus/selection when toolbar buttons are clicked.
    // Without this, clicking a button blurs the editor, clearing the selection
    // before formatLine/formatText can apply the formatting.
    button.addEventListener("mousedown", (event) => {
      event.preventDefault();
    });

    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      // Re-focus the editor so formatting commands have a valid selection context.
      maindiv.focus();
      action(event);
    });
    toolbar.appendChild(button);
  }

  function insertAttachment(attachment: any) {
    const filename = attachment?.attributes?.payload?.body;

    if (editor && attachment) {
      const url = Dmart.getAttachmentUrl({
        resource_type: attachment.resource_type,
        space_name: space_name,
        subpath: subpath,
        parent_shortname: parent_shortname,
        shortname: attachment.shortname,
        ext: getFileExtension(filename),
      });

      const fileExtension = getFileExtension(filename)?.toLowerCase();
      const imageExtensions = [
        "jpg",
        "jpeg",
        "png",
        "gif",
        "webp",
        "svg",
        "bmp",
      ];
      const isImage = imageExtensions.includes(fileExtension);

      if (isImage) {
        const selection = window.getSelection()!;
        const range = selection.getRangeAt(0);

        const img = document.createElement("img");
        img.src = url;
        img.alt = attachment.shortname || "Image";

        range.deleteContents();
        range.insertNode(img);

        range.setStartAfter(img);
        range.setEndAfter(img);
        selection.removeAllRanges();
        selection.addRange(range);
      }

      showAttachments = false;
    }
  }

  function closeAttachments() {
    showAttachments = false;
  }

  function openUrlDialog(kind: "link" | "image") {
    urlValue = "";
    urlDialog = kind;
  }

  function closeUrlDialog() {
    urlDialog = null;
  }

  function applyUrl(event: SubmitEvent) {
    event.preventDefault();
    const url = urlValue.trim();
    const kind = urlDialog;
    if (!url || !kind || !editor) return;
    // Restore the editor's selection context before formatting.
    maindiv.focus();
    if (kind === "link") editor.formatText({ link: url });
    else editor.insert({ image: url });
    urlDialog = null;
  }

  $effect(() => {
    if (editor && typeof editor.setHTML === "function") {
      const currentHtml = editor.getHTML();
      // Ensure content is a string and not null/undefined
      const newContent = content || "";
      if (newContent !== currentHtml) {
        editor.setHTML(newContent);
      }
    }
  });
</script>

<div class="editor-card">
  <div class="editor-content">
    <div
      class="editor-container"
      bind:this={maindiv}
      id="htmleditor-{uid}"
      tabindex="0"
      role="textbox"
    ></div>
  </div>
</div>

{#if showAttachments}
  <Modal title={$_("html_editor.attachments_title")} size="2xl" onClose={closeAttachments}>
    {#if attachments?.media?.length > 0}
      <div class="attachments-grid">
        {#each attachments.media as attachment (attachment.shortname)}
          <div class="attachment-item">
            <div class="attachment-info">
              <div class="attachment-icon" aria-hidden="true">📎</div>
              <div class="attachment-details">
                <div class="attachment-name">
                  {attachment.shortname || $_("html_editor.unnamed")}
                </div>
                <div class="attachment-type">
                  {attachment.resource_type || $_("common.unknown")}
                </div>
              </div>
            </div>
            <button
              type="button"
              class="app-btn app-btn-primary app-btn-sm"
              onclick={() => insertAttachment(attachment)}
            >
              {$_("html_editor.insert")}
            </button>
          </div>
        {/each}
      </div>
    {:else}
      <div class="no-attachments">
        <div class="no-attachments-icon" aria-hidden="true">📎</div>
        <p>{$_("html_editor.no_attachments")}</p>
      </div>
    {/if}
  </Modal>
{/if}

{#if urlDialog}
  <Modal
    title={urlDialog === "link" ? $_("html_editor.link_title") : $_("html_editor.image_title")}
    size="md"
    onClose={closeUrlDialog}
  >
    <form id={urlFormId} onsubmit={applyUrl} class="url-form">
      <label for="{urlFormId}-input" class="url-label">{$_("html_editor.url_label")}</label>
      <input
        id="{urlFormId}-input"
        type="url"
        class="url-input"
        bind:value={urlValue}
        placeholder={$_("html_editor.url_placeholder")}
        required
        data-autofocus
      />
    </form>
    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeUrlDialog}>
        {$_("ui.cancel")}
      </button>
      <button type="submit" form={urlFormId} class="app-btn app-btn-primary">
        {$_("html_editor.insert")}
      </button>
    {/snippet}
  </Modal>
{/if}

<style>
  .editor-card {
    height: 100%;
    max-width: 100%;
    padding: 0.75rem;
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    box-shadow: 0 1px 3px 0 rgba(0, 0, 0, 0.05);
    display: flex;
    flex-direction: column;
  }

  .editor-content {
    max-width: 100%;
    color: var(--color-text);
    line-height: 1.75;
    flex: 1;
    display: flex;
    flex-direction: column;
  }

  .editor-container {
    font-family:
      "uthmantn",
      -apple-system,
      BlinkMacSystemFont,
      "Segoe UI",
      Roboto,
      "Helvetica Neue",
      Arial,
      sans-serif;
    font-size: 1rem !important;
    min-height: 200px;
    max-height: 400px;
    overflow-y: auto;
    border: 1px solid var(--color-border);
    border-radius: 0.375rem;
    padding: 1rem;
    background-color: var(--color-surface-2);
    color: var(--color-text);
    outline: none;
    transition:
      border-color 0.15s ease-in-out,
      box-shadow 0.15s ease-in-out;
    flex: 1; /* FIX: Allow container to grow */
  }

  .editor-container:focus-within {
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .editor-container :global(h1) {
    font-size: 1.875rem;
    font-weight: 700;
    margin: 1.5rem 0 1rem 0;
    color: var(--color-text);
    border-bottom: 2px solid var(--color-border);
    padding-bottom: 0.5rem;
  }

  .editor-container :global(h2) {
    font-size: 1.5rem;
    font-weight: 600;
    margin: 1.25rem 0 0.75rem 0;
    color: var(--color-text);
  }

  .editor-container :global(h3) {
    font-size: 1.25rem;
    font-weight: 600;
    margin: 1rem 0 0.5rem 0;
    color: var(--color-text);
  }

  .editor-container :global(p) {
    margin: 0.75rem 0;
  }

  .editor-container :global(ul),
  .editor-container :global(ol) {
    margin: 0.75rem 0;
    padding-inline-start: 1.5rem;
  }

  .editor-container :global(ul) {
    list-style-type: disc;
  }

  .editor-container :global(ol) {
    list-style-type: decimal;
  }

  .editor-container :global(li) {
    margin: 0.25rem 0;
  }

  .editor-container :global(blockquote) {
    margin: 1rem 0;
    padding: 0.75rem 1rem;
    background: var(--color-surface);
    border-inline-start: 4px solid var(--color-border-strong);
    color: var(--color-text-muted);
  }

  .editor-container :global(code) {
    background: var(--color-surface-3);
    padding: 0.125rem 0.25rem;
    border-radius: 0.25rem;
    font-family: "uthmantn", "Monaco", "Menlo", "Ubuntu Mono", monospace;
    font-size: 0.875rem;
  }

  .editor-container :global(pre) {
    background: var(--color-text);
    color: var(--color-surface);
    padding: 1rem;
    border-radius: 0.5rem;
    overflow-x: auto;
    margin: 1rem 0;
  }

  .editor-container :global(pre code) {
    background: transparent;
    padding: 0;
    color: inherit;
  }

  .editor-container :global(table) {
    width: 100%;
    border-collapse: collapse;
    margin: 1rem 0;
  }

  .editor-container :global(th),
  .editor-container :global(td) {
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    text-align: start;
  }

  .editor-container :global(th) {
    background: var(--color-surface);
    font-weight: 600;
  }

  .editor-container :global(strong) {
    font-weight: 600;
  }

  .editor-container :global(em) {
    font-style: italic;
  }

  .editor-container :global(del) {
    text-decoration: line-through;
  }

  /* FIX: Custom scrollbar styling for better appearance */
  .editor-container::-webkit-scrollbar {
    width: 8px;
  }

  .editor-container::-webkit-scrollbar-track {
    background: var(--color-surface-3);
    border-radius: 4px;
  }

  .editor-container::-webkit-scrollbar-thumb {
    background: var(--color-border-strong);
    border-radius: 4px;
  }

  .editor-container::-webkit-scrollbar-thumb:hover {
    background: var(--color-text-faint);
  }

  :global(.editor-toolbar) {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    padding: 0.75rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-bottom: none;
    border-radius: 0.375rem 0.375rem 0 0;
    margin-bottom: 0;
  }

  :global(.toolbar-group) {
    display: flex;
    gap: 0.25rem;
    padding: 0.25rem;
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: 0.375rem;
  }

  :global(.toolbar-button) {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2rem;
    height: 2rem;
    padding: 0;
    background: transparent;
    border: 1px solid transparent;
    border-radius: 0.25rem;
    color: var(--color-text-muted);
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.15s ease-in-out;
  }

  :global(.toolbar-button:hover) {
    background: var(--color-surface-3);
    border-color: var(--color-border-strong);
    color: var(--color-text);
  }

  :global(.toolbar-button:active) {
    background: var(--color-border);
    transform: translateY(1px);
  }

  .attachments-grid {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .attachment-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1rem;
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    background: var(--color-surface);
    transition: all 0.15s ease-in-out;
  }

  .attachment-item:hover {
    background: var(--color-surface-3);
    border-color: var(--color-border-strong);
  }

  .attachment-info {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex: 1;
  }

  .attachment-icon {
    font-size: 1.5rem;
    color: var(--color-text-muted);
  }

  .attachment-details {
    flex: 1;
  }

  .attachment-name {
    font-weight: 500;
    color: var(--color-text);
    margin-bottom: 0.25rem;
  }

  .attachment-type {
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }

  .no-attachments {
    text-align: center;
    padding: 2rem;
    color: var(--color-text-muted);
  }

  .no-attachments-icon {
    font-size: 3rem;
    margin-bottom: 1rem;
    opacity: 0.5;
  }
  .url-form {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .url-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
  }

  .url-input {
    width: 100%;
    padding: 0.5rem 0.75rem;
    font-size: 0.875rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-control);
    background: var(--color-surface-2);
    color: var(--color-text);
  }

  .url-input:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px var(--color-primary-soft);
  }
</style>
