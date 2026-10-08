<script lang="ts">
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { onMount } from "svelte";
  import { renderMarkdown } from "@/lib/markdown";
  import {
    createEntity,
    getTemplates,
  } from "@/lib/dmart_services";
  import { ContentType, ResourceType } from "@edraj/tsdmart";
  import { goto as gotoStore, params } from "@roxi/routify";
  import { _ } from "@/i18n";
  import { errorMessage } from "@/lib/apiError";
  import { bodyAs, recordsOf, type EntryRecord, type TemplateBody } from "@/lib/types";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  /** A `{{name:type}}` placeholder of a template. */
  interface TemplateField {
    name: string;
    type: string;
  }

  /** What a placeholder is filled with: text, or a flag for a checkbox field. */
  type FieldValue = string | boolean;

  let templates: EntryRecord[] = $state([]);

  $effect(() => setTitle($_("template_generator.title")));
  /** The uuid of the chosen template ("" / null for none). */
  let selectedTemplate: string | null = $state(null);
  let templateFields: TemplateField[] = $state([]);
  let fieldValues: Record<string, FieldValue> = $state({});
  let previewContent = $state("");

  let entityShortname = $state("");
  let entityTags: string[] = $state([]);
  let newTag = $state("");
  let isCreating = $state(false);
  let createMessage = $state("");

  onMount(async () => {
    const response = await getTemplates();
    templates = recordsOf(response);
  });

  /** A template entry's body fields (`title`, `content`). */
  function templateBody(template: EntryRecord): TemplateBody {
    return bodyAs<TemplateBody>(template.attributes?.payload) ?? {};
  }

  /** A field's value as the text its input shows (empty for a blank or false). */
  function textOf(name: string): string {
    return String(fieldValues[name] || "");
  }

  function extractFields(content: string): TemplateField[] {
    const fieldRegex = /\{\{(\w+):(\w+)\}\}/g;
    const fields: TemplateField[] = [];
    let match;

    while ((match = fieldRegex.exec(content)) !== null) {
      const [, name, type] = match;
      fields.push({ name, type });
    }

    return fields;
  }

  function handleTemplateSelect() {
    if (selectedTemplate) {
      const template = templates.find((t) => t.uuid === selectedTemplate);
      if (template) {
        const content = templateBody(template).content ?? "";
        templateFields = extractFields(content);
        fieldValues = {};
        templateFields.forEach((field) => {
          fieldValues[field.name] = "";
        });
        updatePreview();
      }
    } else {
      templateFields = [];
      fieldValues = {};
      previewContent = "";
    }
  }

  /** The template's content with every placeholder replaced by its typed value. */
  function fillTemplate(template: EntryRecord): string {
    let content = templateBody(template).content ?? "";

    templateFields.forEach((field) => {
      const placeholder = `{{${field.name}:${field.type}}}`;
      content = content.replace(placeholder, textOf(field.name));
    });

    return content;
  }

  function updatePreview() {
    if (selectedTemplate) {
      const template = templates.find((t) => t.uuid === selectedTemplate);
      if (template) {
        previewContent = fillTemplate(template);
      }
    }
  }

  function getFieldType(type: string) {
    switch (type) {
      case "string":
        return "text";
      case "int":
      case "float":
      case "number":
        return "number";
      case "date":
        return "date";
      case "text":
        return "textarea";
      case "bool":
        return "checkbox";
      case "list":
      case "object":
      case "list_object":
        return "textarea";
      default:
        return "text";
    }
  }

  function addTag() {
    if (newTag.trim() && !entityTags.includes(newTag.trim())) {
      entityTags = [...entityTags, newTag.trim()];
      newTag = "";
    }
  }

  function removeTag(tagToRemove: string) {
    entityTags = entityTags.filter((tag) => tag !== tagToRemove);
  }

  function handleTagKeypress(event: KeyboardEvent) {
    if (event.key === "Enter") {
      event.preventDefault();
      addTag();
    }
  }

  function getFieldPlaceholder(type: string, name: string) {
    switch (type) {
      case "string":
        return `Enter ${name}...`;
      case "int":
        return `Enter whole number for ${name}...`;
      case "float":
        return `Enter decimal number for ${name}...`;
      case "text":
        return `Enter ${name} text...`;
      case "bool":
        return "";
      case "list":
        return `Enter comma-separated values for ${name}...`;
      case "object":
        return `Enter JSON object for ${name}...`;
      case "list_object":
        return `Enter JSON array of objects for ${name}...`;
      default:
        return `Enter ${name}...`;
    }
  }

  async function handleCreate() {
    if (!selectedTemplate) {
      createMessage = "Please select a template first";
      return;
    }

    const template = templates.find((t) => t.uuid === selectedTemplate);
    if (!template) {
      createMessage = "Template not found";
      return;
    }

    const emptyFields = templateFields.filter(
      (field) => {
        const value = fieldValues[field.name];
        // A text field is empty when blank; a checkbox always counts as filled.
        if (value === undefined || value === null) return true;
        if (typeof value === "string") return !value.trim();
        return false;
      },
    );
    if (emptyFields.length > 0) {
      createMessage = `Please fill in all fields: ${emptyFields.map((f) => f.name).join(", ")}`;
      return;
    }

    isCreating = true;
    createMessage = "";

    try {
      const content = fillTemplate(template);

      const entityData = {
        shortname: entityShortname.trim() || "auto",
        tags: entityTags,
        is_active: true,
        body: content,
      };

      const attributes = {
        displayname: { en: entityData.shortname || "auto" },
        description: { en: "", ar: "", ku: "" },
        is_active: true,
        tags: entityTags || [],
        relationships: [],
        payload: {
          content_type: ContentType.markdown || "md",
          schema_shortname: "templates",
          body: content,
        },
      };

      const result = await createEntity(
        $params.space_name,
        $params.subpath,
        ResourceType.content,
        attributes,
        entityData.shortname || "auto",
      );

      if (result) {
        createMessage = `Entity created successfully with shortname: ${result}`;
        goto(`/dashboard/admin/${$params.space_name}/templates`);
        resetForm();
      } else {
        createMessage = "Failed to create entity";
      }
    } catch (error) {
      log.error("Error creating entity:", error);
      createMessage = "Error creating entity: " + errorMessage(error);
    } finally {
      isCreating = false;
    }
  }

  function resetForm() {
    selectedTemplate = null;
    templateFields = [];
    fieldValues = {};
    previewContent = "";
    entityShortname = "";
    entityTags = [];
    newTag = "";
  }

  $effect(() => {
    if (selectedTemplate && Object.keys(fieldValues).length > 0) {
      updatePreview();
    }
  });
</script>

<div class="container">
  <h1>{$_("template_generator.title")}</h1>

  <div class="form-section">
    <div class="field-group">
      <label for="template-select">{$_("template_generator.select_template")}</label>
      <select
        id="template-select"
        bind:value={selectedTemplate}
        onchange={handleTemplateSelect}
      >
        <option value="">{$_("template_generator.choose_template")}</option>
        {#each templates as template (template.uuid)}
          <option value={template.uuid}>
            {templateBody(template).title}
          </option>
        {/each}
      </select>
    </div>

    {#if templateFields.length > 0}
      <h3>{$_("template_generator.fill_fields")}</h3>
      {#each templateFields as field (field.name)}
        <div class="field-group">
          <label for={field.name}>
            {field.name} ({field.type})
          </label>
          {#if getFieldType(field.type) === "textarea"}
            <textarea
              id={field.name}
              value={textOf(field.name)}
              oninput={(e) => (fieldValues[field.name] = e.currentTarget.value)}
              placeholder={getFieldPlaceholder(field.type, field.name)}
              rows={field.type === "list" || field.type === "object" || field.type === "list_object" ? 5 : 3}
            ></textarea>
            {#if field.type === "list"}
              <small class="field-hint">{$_("json_editor.csv_placeholder")}</small>
            {:else if field.type === "object"}
              <small class="field-hint">{$_("template_generator.hint_json_object")}</small>
            {:else if field.type === "list_object"}
              <small class="field-hint">{$_("template_generator.hint_json_array")}</small>
            {/if}
          {:else if getFieldType(field.type) === "checkbox"}
            <input
              id={field.name}
              type="checkbox"
              checked={fieldValues[field.name] === true}
              onchange={(e) => (fieldValues[field.name] = e.currentTarget.checked)}
            />
          {:else}
            <input
              id={field.name}
              type={getFieldType(field.type)}
              value={textOf(field.name)}
              oninput={(e) => (fieldValues[field.name] = e.currentTarget.value)}
              placeholder={getFieldPlaceholder(field.type, field.name)}
            />
          {/if}
        </div>
      {/each}
    {/if}
  </div>

  {#if selectedTemplate}
    <div class="create-section">
      <h3>{$_("template_generator.create_entity")}</h3>

      <div class="field-group">
        <label for="entity-shortname">{$_("fields.shortname")}</label>
        <input
          id="entity-shortname"
          type="text"
          bind:value={entityShortname}
          placeholder={$_("route_labels.placeholder_leave_empty_auto")}
        />
      </div>

      <div class="field-group">
        <p class="field-label">{$_("admin_space.config.fields.tags")}</p>
        <div class="tags-container">
          {#each entityTags as tag (tag)}
            <span class="tag">
              {tag}
              <button
                type="button"
                class="remove-tag"
                aria-label={$_("entry_edit.remove_tag")}
                onclick={() => removeTag(tag)}>×</button
              >
            </span>
          {/each}
        </div>
        <div class="tag-input-container">
          <input
            class="tag-input"
            type="text"
            bind:value={newTag}
            onkeypress={handleTagKeypress}
            placeholder={$_("route_labels.placeholder_add_tag")}
          />
          <button
            class="add-tag-btn"
            onclick={addTag}
            disabled={!newTag.trim()}
          >
            Add Tag
          </button>
        </div>
      </div>

      <button
        class="create-button"
        onclick={handleCreate}
        disabled={isCreating || !selectedTemplate}
      >
        {#if isCreating}
          <span class="loading"></span>
        {/if}
        {isCreating ? "Creating..." : "Create Entity"}
      </button>

      {#if createMessage}
        <div
          class="create-message {createMessage.includes('success')
            ? 'success'
            : 'error'}"
        >
          {createMessage}
        </div>
      {/if}
    </div>
  {/if}

  {#if selectedTemplate}
    <div class="preview-section">
      <h3>{$_("labels.preview")}</h3>

      <div class="two-column">
        <div>
          <h4>{$_("template_generator.raw_content")}</h4>
          {#if previewContent}
            <div class="preview-content">{previewContent}</div>
          {:else}
            <div class="empty-state">{$_("template_generator.preview_hint")}</div>
          {/if}
        </div>

        <div>
          <h4>{$_("template_generator.rendered_markdown")}</h4>
          {#if previewContent}
            <div class="markdown-preview">
              {@html renderMarkdown(previewContent)}
            </div>
          {:else}
            <div class="empty-state">
              Fill in the fields to see rendered preview
            </div>
          {/if}
        </div>
      </div>
    </div>
  {/if}
</div>

<style>
  .container {
    max-width: 1200px;
    margin: 0 auto;
    padding: 20px;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto,
      sans-serif;
  }

  .form-section {
    background: var(--color-surface);
    padding: 20px;
    border-radius: 8px;
    margin-bottom: 20px;
  }

  .field-group {
    margin-bottom: 15px;
  }

  label {
    display: block;
    margin-bottom: 5px;
    font-weight: 600;
    color: var(--color-text);
    text-transform: capitalize;
  }

  select,
  input,
  textarea {
    width: 100%;
    padding: 10px;
    border: 1px solid var(--color-border);
    border-radius: 4px;
    font-size: 14px;
    box-sizing: border-box;
  }

  select:focus,
  input:focus,
  textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 2px rgba(0, 123, 255, 0.25);
  }

  textarea {
    resize: vertical;
    min-height: 80px;
  }

  .preview-section {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 8px;
    padding: 20px;
  }

  .preview-section h3 {
    margin-top: 0;
    color: var(--color-text);
    border-bottom: 2px solid var(--color-border);
    padding-bottom: 10px;
  }

  .preview-content {
    background: var(--color-surface);
    border: 1px solid var(--color-surface-3);
    border-radius: 4px;
    padding: 15px;
    margin: 10px 0;
    white-space: pre-wrap;
    font-family: "Monaco", "Menlo", monospace;
    font-size: 13px;
    line-height: 1.5;
  }

  .markdown-preview {
    background: var(--color-surface);
    border: 1px solid var(--color-surface-3);
    border-radius: 4px;
    padding: 15px;
    line-height: 1.6;
  }

  .markdown-preview :global(h1) {
    border-bottom: 1px solid var(--color-border);
    padding-bottom: 10px;
  }

  .markdown-preview :global(h1),
  .markdown-preview :global(h2),
  .markdown-preview :global(h3),
  .markdown-preview :global(h4),
  .markdown-preview :global(h5),
  .markdown-preview :global(h6) {
    margin-top: 24px;
    margin-bottom: 16px;
    font-weight: 600;
    line-height: 1.25;
  }

  .markdown-preview :global(p) {
    margin-bottom: 16px;
  }

  .markdown-preview :global(strong) {
    font-weight: 600;
  }

  .two-column {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 20px;
  }

  .tag {
    display: inline-block;
    background: var(--color-primary);
    color: white;
    padding: 4px 8px;
    border-radius: 12px;
    font-size: 12px;
    margin-inline-end: 8px;
    margin-bottom: 8px;
  }

  .tag .remove-tag {
    margin-inline-start: 6px;
    padding: 0;
    border: none;
    background: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
    font-weight: bold;
  }

  .tag .remove-tag:hover {
    color: var(--color-danger-soft);
  }

  .tags-container {
    margin-bottom: 10px;
    min-height: 20px;
  }

  .tag-input-container {
    display: flex;
    gap: 10px;
    align-items: center;
  }

  .tag-input {
    flex: 1;
  }

  .add-tag-btn {
    background: var(--color-success);
    color: white;
    border: none;
    padding: 10px 15px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 14px;
  }

  .add-tag-btn:hover {
    background: var(--color-success);
  }

  .add-tag-btn:disabled {
    background: var(--color-text-muted);
    cursor: not-allowed;
  }

  .create-section {
    background: var(--color-success-soft);
    border: 1px solid var(--color-success-soft);
    border-radius: 8px;
    padding: 20px;
    margin-top: 20px;
  }

  .create-button {
    background: var(--color-primary);
    color: white;
    border: none;
    padding: 12px 24px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 16px;
    font-weight: 600;
    width: 100%;
    margin-top: 15px;
  }

  .create-button:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }

  .create-button:disabled {
    background: var(--color-text-muted);
    cursor: not-allowed;
  }

  .create-message {
    margin-top: 15px;
    padding: 10px;
    border-radius: 4px;
    font-weight: 500;
  }

  .create-message.success {
    background: var(--color-success-soft);
    color: var(--color-success);
    border: 1px solid var(--color-success-soft);
  }

  .create-message.error {
    background: var(--color-danger-soft);
    color: var(--color-danger);
    border: 1px solid var(--color-danger-soft);
  }

  .loading {
    display: inline-block;
    width: 16px;
    height: 16px;
    border: 2px solid var(--color-surface-2);
    border-radius: 50%;
    border-top-color: transparent;
    animation: spin 1s ease-in-out infinite;
    margin-inline-end: 8px;
  }

  @media (max-width: 768px) {
    .two-column {
      grid-template-columns: 1fr;
    }
  }

  .empty-state {
    text-align: center;
    color: var(--color-text-muted);
    font-style: italic;
    padding: 40px;
    background: var(--color-surface);
    border-radius: 4px;
  }

  .field-hint {
    display: block;
    margin-top: 6px;
    font-size: 12px;
    color: var(--color-text-muted);
    font-style: italic;
  }
</style>
