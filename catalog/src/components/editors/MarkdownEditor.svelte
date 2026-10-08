<script lang="ts">
  import { log } from "@/lib/logger";
  import { _ } from "@/i18n";
  import { renderMarkdown } from "@/lib/markdown";
  import { Dmart } from "@edraj/tsdmart";
  import { getFileExtension } from "@shared/file-extension";
  import { isImageFile } from "@/lib/fileUtils";
  import { attachmentMarkdown } from "@/lib/markdownInsert";

  interface FieldType {
    value: string;
    label: string;
    description: string;
  }

  interface Props {
    content?: string;
    handleSave?: any;
    enableDynamicContent?: boolean;
    onDropKey?: ((key: { name: string; type: string }) => void) | null;
    // Media insertion. All optional: without them the attachments button is
    // simply not rendered, so the editor keeps working at the call sites that
    // have no parent entry to attach to (template editing, entry creation).
    isEditMode?: boolean;
    attachments?: any;
    space_name?: string;
    subpath?: string;
    parent_shortname?: string;
  }

  let {
    content = $bindable(""),
    handleSave = () => {},
    enableDynamicContent = true,
    onDropKey = null as any,
    isEditMode = false,
    attachments = null,
    space_name = "",
    subpath = "",
    parent_shortname = "",
  }: Props = $props();

  const fieldTypes: FieldType[] = [
    { value: "string", label: "String", description: "Short text value" },
    { value: "text", label: "Text", description: "Long text content" },
    { value: "int", label: "Integer", description: "Whole number" },
    { value: "float", label: "Float", description: "Decimal number" },
    { value: "bool", label: "Boolean", description: "True/False value" },
    { value: "list", label: "List", description: "Array of values" },
    { value: "object", label: "Object", description: "Single object" },
    { value: "list_object", label: "List Object", description: "Array of objects" },
  ];

  if (typeof content !== "string") {
    content = "";
  }

  let textarea: any;
  let activeTab = $state("editor");
  let start = 0,
    end = 0;
  let showDynamicMenu = $state(false);
  let dynamicFieldName = $state("");
  let selectedFieldType = $state("string");
  let dynamicMenuRef: HTMLDivElement = $state(undefined as any);
  let isDraggingOver = $state(false);
  let showAttachments = $state(false);

  // Only media attachments are offered: the picker exists to put a picture in
  // the prose, and a json/comment attachment has no meaningful markdown form.
  let mediaAttachments = $derived(attachments?.media ?? []);

  function handleSelect() {
    start = textarea.selectionStart;
    end = textarea.selectionEnd;
  }

  function insertDynamicContent() {
    if (!dynamicFieldName.trim()) return;
    
    const placeholder = `{{${dynamicFieldName.trim()}:${selectedFieldType}}}`;
    const cursorPos = textarea.selectionStart;
    
    const before = content.substring(0, cursorPos);
    const after = content.substring(cursorPos);
    
    content = before + placeholder + after;
    handleSave();
    
    // Reset and close menu
    dynamicFieldName = "";
    selectedFieldType = "string";
    showDynamicMenu = false;
    
    // Set cursor after the inserted placeholder
    setTimeout(() => {
      const newCursorPos = cursorPos + placeholder.length;
      textarea.setSelectionRange(newCursorPos, newCursorPos);
      textarea.focus();
    }, 0);
  }

  function toggleDynamicMenu() {
    showDynamicMenu = !showDynamicMenu;
    if (showDynamicMenu) {
      // Reset fields when opening
      dynamicFieldName = "";
      selectedFieldType = "string";
    }
  }

  function handleMenuClickOutside(event: MouseEvent) {
    if (dynamicMenuRef && !dynamicMenuRef.contains(event.target as Node)) {
      showDynamicMenu = false;
    }
  }

  function handleMenuKeyDown(event: KeyboardEvent) {
    if (event.key === "Escape") {
      showDynamicMenu = false;
    } else if (event.key === "Enter" && dynamicFieldName.trim()) {
      event.preventDefault();
      insertDynamicContent();
    }
  }

  function handleDragOver(event: DragEvent) {
    if (onDropKey) {
      event.preventDefault();
      event.dataTransfer!.dropEffect = "copy";
      isDraggingOver = true;
    }
  }

  function handleDragLeave() {
    isDraggingOver = false;
  }

  function handleDrop(event: DragEvent) {
    if (!onDropKey) return;
    
    event.preventDefault();
    isDraggingOver = false;
    
    try {
      const keyData = event.dataTransfer!.getData("application/json");
      if (keyData) {
        const key = JSON.parse(keyData);
        insertKeyAtCursor(key);
        onDropKey(key);
      }
    } catch (e) {
      log.error("Error handling drop:", e);
    }
  }

  // Splice `snippet` in at the caret and leave the caret after it. Extracted
  // from insertKeyAtCursor so attachment insertion reuses the same mechanics
  // rather than reimplementing them a second way.
  function insertAtCursor(snippet: string) {
    const cursorPos = textarea?.selectionStart ?? content.length;

    const before = content.substring(0, cursorPos);
    const after = content.substring(cursorPos);

    content = before + snippet + after;
    handleSave();

    setTimeout(() => {
      const newCursorPos = cursorPos + snippet.length;
      textarea?.setSelectionRange(newCursorPos, newCursorPos);
      textarea?.focus();
    }, 0);
  }

  function insertKeyAtCursor(key: { name: string; type: string }) {
    insertAtCursor(`{{${key.name}:${key.type}}}`);
  }

  // Insert a media attachment of the entry being edited as markdown.
  //
  // The HTML editor has had this since forever; markdown did not, so authors
  // had to hand-type the attachment URL — which meant knowing the
  // /managed/payload URL shape by heart. Same picker, same URL builder, output
  // as markdown instead of a DOM node.
  function insertAttachment(attachment: any) {
    if (!attachment) return;

    const filename = attachment?.attributes?.payload?.body ?? "";
    const url = Dmart.getAttachmentUrl({
      resource_type: attachment.resource_type,
      space_name,
      subpath,
      parent_shortname,
      shortname: attachment.shortname,
      // tsdmart appends `.${ext}` unless ext is literally null, and
      // getFileExtension returns "" for a name with no dot — which would emit a
      // URL ending in a bare dot. Harmless in a DOM node, but markdown shows
      // the URL as text, so normalise "" to null.
      ext: getFileExtension(filename) || null,
    });

    const label = attachment.shortname || filename || "attachment";
    insertAtCursor(attachmentMarkdown(label, url, filename));

    showAttachments = false;
  }

  function closeAttachments() {
    showAttachments = false;
  }

  function handleAttachmentsModalClick(event: any) {
    if (event.target === event.currentTarget) closeAttachments();
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

  function handleKeyDown(event: any) {
    if (event.ctrlKey) {
      if (["b", "i", "t"].includes(event.key)) {
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
  }

  function handleFormatting(format: any, isWrap = true, isPerLine = false) {
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
        let lineStart =
          textarea.value.substring(0, start).split("\n").length - 1;
        let lineEnd = textarea.value.substring(0, end).split("\n").length - 1;

        if (textarea.value[end] === "\n") {
          lineEnd--;
        }

        for (let i = lineStart; i <= lineEnd; i++) {
          lines[i] = `${format} ` + lines[i];
        }

        textarea.value = lines.join("\n");
      } else {
        let lineStart = textarea.value.lastIndexOf("\n", start - 1) + 1;
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
    content = structuredClone(textarea.value);
  }

  export function getContent() {
    return renderMarkdown(content);
  }

  // Tab switching functionality
  function switchTab(tabName: any) {
    activeTab = tabName;
  }

  // Handle click outside to close dynamic menu
  $effect(() => {
    if (showDynamicMenu) {
      document.addEventListener("click", handleMenuClickOutside);
      return () => {
        document.removeEventListener("click", handleMenuClickOutside);
      };
    }
  });

</script>

<div class="markdown-editor-container">
  <div class="editor-toolbar">
    <div class="toolbar-group">
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("**")}
        title={$_("html_editor.toolbar.bold")} aria-label={$_("html_editor.toolbar.bold")}
      >
        <strong>B</strong>
      </button>
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("_")}
        title={$_("html_editor.toolbar.italic")} aria-label={$_("html_editor.toolbar.italic")}
      >
        <i>I</i>
      </button>
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("~~")}
        title={$_("html_editor.toolbar.strike")} aria-label={$_("html_editor.toolbar.strike")}
      >
        <del>S</del>
      </button>
    </div>

    <div class="toolbar-group">
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("*", false, true)}
        title={$_("html_editor.toolbar.unordered_list")} aria-label={$_("html_editor.toolbar.unordered_list")}
      >
        <span>•</span>
      </button>
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("1.", false, true)}
        title={$_("html_editor.toolbar.ordered_list")} aria-label={$_("html_editor.toolbar.ordered_list")}
      >
        <span>1.</span>
      </button>
    </div>

    <div class="toolbar-group">
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("#", false)}
        title={$_("html_editor.toolbar.heading_1")} aria-label={$_("html_editor.toolbar.heading_1")}
      >
        <span>H1</span>
      </button>
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("##", false)}
        title={$_("html_editor.toolbar.heading_2")} aria-label={$_("html_editor.toolbar.heading_2")}
      >
        <span>H2</span>
      </button>
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting("###", false)}
        title={$_("markdown_editor.heading_3")} aria-label={$_("markdown_editor.heading_3")}
      >
        <span>H3</span>
      </button>
    </div>

    <div class="toolbar-group">
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting(tableInsert, false)}
        title={$_("markdown_editor.insert_table")} aria-label={$_("markdown_editor.insert_table")}
      >
        <span>⊞</span>
      </button>
      <button
        class="toolbar-btn"
        onclick={() => handleFormatting(listViewInsert, false)}
        title={$_("markdown_editor.insert_list_view")} aria-label={$_("markdown_editor.insert_list_view")}
      >
        <span>☰</span>
      </button>
    </div>

    {#if isEditMode && mediaAttachments.length > 0}
      <div class="toolbar-group">
        <button
          class="toolbar-btn"
          onclick={() => (showAttachments = true)}
          title={$_("html_editor.toolbar.attachments")} aria-label={$_("html_editor.toolbar.attachments")}
        >
          <span>📎</span>
        </button>
      </div>
    {/if}

    {#if enableDynamicContent}
      <div class="toolbar-group dynamic-content-group">
        <div class="dynamic-content-wrapper" bind:this={dynamicMenuRef}>
          <button
            class="toolbar-btn dynamic-btn"
            onclick={toggleDynamicMenu}
            title={$_("markdown_editor.insert_dynamic")} aria-label={$_("markdown_editor.insert_dynamic")}
            class:active={showDynamicMenu}
          >
            <span>{`{ }`}</span>
          </button>
          
          {#if showDynamicMenu}
            <div 
              class="dynamic-menu" 
              onclick={(e) => e.stopPropagation()}
              onkeydown={handleMenuKeyDown}
              role="dialog"
              aria-label={$_("markdown_editor.dynamic_region")}
              tabindex="-1"
            >
              <div class="dynamic-menu-header">
                <span>{$_("markdown_editor.insert_dynamic_field")}</span>
              </div>
              <div class="dynamic-menu-body">
                <div class="field-input-group">
                  <label for="field-name">{$_("json_editor.field_name_label")}</label>
                  <input
                    id="field-name"
                    type="text"
                    bind:value={dynamicFieldName}
                    placeholder={$_("markdown_editor.field_name_placeholder")}
                    onkeydown={handleMenuKeyDown}
                  />
                </div>
                <div class="field-input-group">
                  <label for="field-type">{$_("json_editor.field_type_label")}</label>
                  <select id="field-type" bind:value={selectedFieldType}>
                    {#each fieldTypes as type (type.value)}
                      <option value={type.value} title={type.description}>
                        {type.label}
                      </option>
                    {/each}
                  </select>
                </div>
                <div class="field-preview">
                  <code>&#123;&#123;{dynamicFieldName ? `${dynamicFieldName}:${selectedFieldType}` : 'field_name:type'}&#125;&#125;</code>
                </div>
              </div>
              <div class="dynamic-menu-footer">
                <button 
                  class="btn-insert" 
                  onclick={insertDynamicContent}
                  disabled={!dynamicFieldName.trim()}
                >
                  Insert
                </button>
                <button class="btn-cancel" onclick={() => showDynamicMenu = false}>
                  Cancel
                </button>
              </div>
            </div>
          {/if}
        </div>
      </div>
    {/if}
  </div>

  <div class="editor-tabs">
    <div class="tab-buttons">
      <button
        class="tab-btn active"
        data-tab="editor"
        onclick={() => switchTab("editor")}>Editor</button
      >
      <button
        class="tab-btn"
        data-tab="preview"
        onclick={() => switchTab("preview")}>Preview</button
      >
    </div>

    <div class="tab-content">
      <div
        class="tab-panel {activeTab === 'editor' ? 'active' : ''}"
        data-panel="editor"
      >
        <textarea
          bind:this={textarea}
          onselect={handleSelect}
          onkeydown={handleKeyDown}
          ondragover={handleDragOver}
          ondragleave={handleDragLeave}
          ondrop={handleDrop}
          rows="20"
          maxlength="4096"
          class="markdown-textarea {isDraggingOver ? 'drag-over' : ''}"
          bind:value={content}
          oninput={() => handleSave()}
          placeholder={$_("markdown_editor.content_placeholder")}
        ></textarea>
      </div>
      <div
        class="tab-panel {activeTab === 'preview' ? 'active' : ''}"
        data-panel="preview"
      >
        <div class="markdown-preview">
          {@html renderMarkdown(content)}
        </div>
      </div>
    </div>
  </div>
</div>

{#if showAttachments}
  <div
    class="md-attachments-overlay"
    role="dialog"
    aria-modal="true"
    aria-label={$_("html_editor.toolbar.attachments")}
    tabindex="-1"
    onclick={handleAttachmentsModalClick}
    onkeydown={(e) => {
      if (e.key === "Escape") closeAttachments();
    }}
  >
    <!-- The inner stopPropagation exists so a click on the panel does not reach
         the overlay's close handler; role=presentation says it carries no
         semantics of its own, which is what keeps that legitimate. -->
    <div
      class="md-attachments-modal"
      role="presentation"
      onclick={(e) => e.stopPropagation()}
    >
      <div class="md-attachments-header">
        <h3>{$_("html_editor.toolbar.attachments")}</h3>
        <button
          class="md-attachments-close"
          aria-label={$_("html_editor.close_attachments")}
          onclick={closeAttachments}>✕</button
        >
      </div>
      <div class="md-attachments-grid">
        {#each mediaAttachments as attachment (attachment.shortname)}
          <button
            type="button"
            class="md-attachment-item"
            onclick={() => insertAttachment(attachment)}
            title={attachment?.attributes?.payload?.body ?? attachment.shortname}
          >
            <span class="md-attachment-icon">
              {isImageFile(attachment?.attributes?.payload?.body ?? "") ? "🖼️" : "📎"}
            </span>
            <span class="md-attachment-name">{attachment.shortname}</span>
          </button>
        {/each}
      </div>
    </div>
  </div>
{/if}


<style>

  /* Attachment picker. Namespaced md- because the HTML editor ships its own
     .attachments-* classes and these two editors can render on the same page. */
  .md-attachments-overlay {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
  }
  .md-attachments-modal {
    background: var(--md-surface, var(--color-surface-2));
    color: inherit;
    border-radius: 8px;
    width: min(560px, 92vw);
    max-height: 80vh;
    overflow: auto;
    box-shadow: 0 10px 30px rgba(0, 0, 0, 0.25);
  }
  .md-attachments-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px;
    border-bottom: 1px solid rgba(0, 0, 0, 0.1);
  }
  .md-attachments-header h3 {
    margin: 0;
    font-size: 1rem;
  }
  .md-attachments-close {
    border: 0;
    background: transparent;
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 4px 8px;
  }
  .md-attachments-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
    gap: 10px;
    padding: 16px;
  }
  .md-attachment-item {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    padding: 12px 8px;
    border: 1px solid rgba(0, 0, 0, 0.12);
    border-radius: 6px;
    background: transparent;
    cursor: pointer;
    font: inherit;
    color: inherit;
  }
  .md-attachment-item:hover,
  .md-attachment-item:focus-visible {
    border-color: rgba(0, 0, 0, 0.35);
  }
  .md-attachment-icon {
    font-size: 1.5rem;
  }
  .md-attachment-name {
    font-size: 0.8rem;
    overflow-wrap: anywhere;
    text-align: center;
  }

  .markdown-editor-container {
    height: 100%;
    display: flex;
    flex-direction: column;
    background: white;
    border-radius: 0.75rem;
    overflow: hidden;
    border: 1px solid var(--color-border);
  }

  .editor-toolbar {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.75rem 1rem;
    background: var(--color-surface);
    border-bottom: 1px solid var(--color-border);
    flex-wrap: wrap;
  }

  .toolbar-group {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0 0.5rem;
    border-inline-end: 1px solid var(--color-border-strong);
  }

  .toolbar-group:last-child {
    border-inline-end: none;
  }

  .toolbar-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2rem;
    height: 2rem;
    background: white;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.375rem;
    color: var(--color-text);
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .toolbar-btn:hover {
    background: var(--color-surface-3);
    border-color: var(--color-text-faint);
  }

  .toolbar-btn:active {
    background: var(--color-border);
  }

  .editor-tabs {
    flex: 1;
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  .tab-buttons {
    display: flex;
    background: var(--color-surface);
    border-bottom: 1px solid var(--color-border);
  }

  .tab-btn {
    padding: 0.75rem 1.5rem;
    background: transparent;
    border: none;
    color: var(--color-text-muted);
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s ease;
    border-bottom: 2px solid transparent;
  }

  .tab-btn:hover {
    color: var(--color-text);
    background: var(--color-surface-3);
  }

  .tab-btn.active {
    color: var(--color-primary-hover);
    background: white;
    border-bottom-color: var(--color-primary-hover);
  }

  .tab-content {
    flex: 1;
    position: relative;
    overflow: hidden;
  }

  .tab-panel {
    position: absolute;
    top: 0;
    inset-inline-start: 0;
    inset-inline-end: 0;
    bottom: 0;
    opacity: 0;
    visibility: hidden;
    transition: all 0.2s ease;
  }

  .tab-panel.active {
    opacity: 1;
    visibility: visible;
  }

  .markdown-textarea {
    width: 100%;
    height: 100%;
    padding: 1rem;
    border: none;
    outline: none;
    resize: none;
    font-family:
      "uthmantn",
      -apple-system,
      BlinkMacSystemFont,
      "Segoe UI",
      Roboto,
      "Helvetica Neue",
      Arial,
      sans-serif;
    font-size: 0.875rem;
    line-height: 1.6;
    background: white;
    color: var(--color-text);
    transition: background-color 0.2s ease;
  }

  .markdown-textarea.drag-over {
    background: var(--color-info-soft);
    box-shadow: inset 0 0 0 3px var(--color-primary);
  }

  .markdown-preview {
    height: 100%;
    padding: 1rem;
    overflow-y: auto;
    background: white;
    font-family:
      "uthmantn",
      -apple-system,
      BlinkMacSystemFont,
      "Segoe UI",
      Roboto,
      "Helvetica Neue",
      Arial,
      sans-serif;
    line-height: 1.6;
    color: var(--color-text);
  }

  .markdown-preview :global(h1) {
    font-size: 1.875rem;
    font-weight: 700;
    margin: 1.5rem 0 1rem 0;
    color: var(--color-text);
    border-bottom: 2px solid var(--color-border);
    padding-bottom: 0.5rem;
  }

  .markdown-preview :global(h2) {
    font-size: 1.5rem;
    font-weight: 600;
    margin: 1.25rem 0 0.75rem 0;
    color: var(--color-text);
  }

  .markdown-preview :global(h3) {
    font-size: 1.25rem;
    font-weight: 600;
    margin: 1rem 0 0.5rem 0;
    color: var(--color-text);
  }

  .markdown-preview :global(p) {
    margin: 0.75rem 0;
  }

  .markdown-preview :global(ul),
  .markdown-preview :global(ol) {
    margin: 0.75rem 0;
    padding-inline-start: 1.5rem;
  }

  .markdown-preview :global(ul) {
    list-style-type: disc;
  }

  .markdown-preview :global(ol) {
    list-style-type: decimal;
  }

  .markdown-preview :global(li) {
    margin: 0.25rem 0;
  }

  .markdown-preview :global(blockquote) {
    margin: 1rem 0;
    padding: 0.75rem 1rem;
    background: var(--color-surface);
    border-inline-start: 4px solid var(--color-border-strong);
    color: var(--color-text-muted);
  }

  .markdown-preview :global(code) {
    background: var(--color-surface-3);
    padding: 0.125rem 0.25rem;
    border-radius: 0.25rem;
    font-family: "uthmantn", "Monaco", "Menlo", "Ubuntu Mono", monospace;
    font-size: 0.875rem;
  }

  .markdown-preview :global(pre) {
    background: var(--color-text);
    color: var(--color-surface);
    padding: 1rem;
    border-radius: 0.5rem;
    overflow-x: auto;
    margin: 1rem 0;
  }

  .markdown-preview :global(pre code) {
    background: transparent;
    padding: 0;
    color: inherit;
  }

  .markdown-preview :global(table) {
    width: 100%;
    border-collapse: collapse;
    margin: 1rem 0;
  }

  .markdown-preview :global(th),
  .markdown-preview :global(td) {
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    text-align: start;
  }

  .markdown-preview :global(th) {
    background: var(--color-surface);
    font-weight: 600;
  }

  .markdown-preview :global(strong) {
    font-weight: 600;
  }

  .markdown-preview :global(em) {
    font-style: italic;
  }

  .markdown-preview :global(del) {
    text-decoration: line-through;
  }

  /* Dynamic Content Menu Styles */
  .dynamic-content-wrapper {
    position: relative;
  }

  .dynamic-btn {
    font-family: monospace;
    font-weight: 600;
  }

  .dynamic-btn.active {
    background: var(--color-primary-hover);
    color: white;
    border-color: var(--color-primary-hover);
  }

  .dynamic-menu {
    position: absolute;
    top: calc(100% + 8px);
    inset-inline-end: 0;
    width: 280px;
    background: white;
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.1), 0 4px 6px -2px rgba(0, 0, 0, 0.05);
    z-index: 100;
    overflow: hidden;
  }

  .dynamic-menu-header {
    padding: 0.75rem 1rem;
    background: var(--color-surface);
    border-bottom: 1px solid var(--color-border);
    font-weight: 600;
    font-size: 0.875rem;
    color: var(--color-text);
  }

  .dynamic-menu-body {
    padding: 1rem;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .field-input-group {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .field-input-group label {
    font-size: 0.75rem;
    font-weight: 500;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.025em;
  }

  .field-input-group input,
  .field-input-group select {
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.375rem;
    font-size: 0.875rem;
    background: white;
    color: var(--color-text);
    transition: border-color 0.15s ease, box-shadow 0.15s ease;
  }

  .field-input-group input:focus,
  .field-input-group select:focus {
    outline: none;
    border-color: var(--color-primary-hover);
    box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.1);
  }

  .field-preview {
    padding: 0.5rem 0.75rem;
    background: var(--color-surface-3);
    border-radius: 0.375rem;
    font-size: 0.8125rem;
  }

  .field-preview code {
    color: var(--color-primary-hover);
    font-family: "Monaco", "Menlo", "Ubuntu Mono", monospace;
  }

  .dynamic-menu-footer {
    display: flex;
    justify-content: flex-end;
    gap: 0.5rem;
    padding: 0.75rem 1rem;
    background: var(--color-surface);
    border-top: 1px solid var(--color-border);
  }

  .btn-insert,
  .btn-cancel {
    padding: 0.5rem 1rem;
    border-radius: 0.375rem;
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.15s ease;
    border: none;
  }

  .btn-insert {
    background: var(--color-primary-hover);
    color: white;
  }

  .btn-insert:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }

  .btn-insert:disabled {
    background: var(--color-border-strong);
    cursor: not-allowed;
  }

  .btn-cancel {
    background: white;
    color: var(--color-text-muted);
    border: 1px solid var(--color-border-strong);
  }

  .btn-cancel:hover {
    background: var(--color-surface-3);
    color: var(--color-text);
  }

  @media (max-width: 768px) {
    .editor-toolbar {
      padding: 0.5rem;
      gap: 0.25rem;
    }

    .toolbar-group {
      padding: 0 0.25rem;
    }

    .toolbar-btn {
      width: 1.75rem;
      height: 1.75rem;
      font-size: 0.75rem;
    }

    .tab-btn {
      padding: 0.5rem 1rem;
      font-size: 0.875rem;
    }

    .markdown-textarea,
    .markdown-preview {
      padding: 0.75rem;
    }

    .dynamic-menu {
      position: fixed;
      top: 50%;
      inset-inline-start: 50%;
      transform: translate(-50%, -50%);
      width: calc(100vw - 2rem);
      max-width: 320px;
      inset-inline-end: auto;
    }
  }
</style>
