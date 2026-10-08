<script lang="ts">
  import { _ } from "@/i18n";
  import { onMount } from "svelte";
  import { getTemplates } from "@/lib/dmart_services";
  import { bodyAs, isJsonObject, recordsOf, type EntryRecord, type TemplateBody } from "@/lib/types";

  /** A `{{name:type}}` placeholder of a template. */
  interface TemplateField {
    name: string;
    type: string;
  }

  /** What a placeholder is filled with: text, or a number / flag for typed fields. */
  type FieldValue = string | number | boolean;

  let {
    content = $bindable(""),
    space_name = "",
    onContentChange = () => {},
  }: {
    content?: string;
    space_name?: string;
    onContentChange?: (content: string) => void;
  } = $props();

  let templates: EntryRecord[] = $state([]);
  let originalTemplate: EntryRecord | null = $state(null);
  let templateFields: TemplateField[] = $state([]);
  let fieldValues: Record<string, FieldValue> = $state({});

  /** A field's value as the text its input shows (empty for a blank or false). */
  function textOf(name: string): string {
    return String(fieldValues[name] || "");
  }

  // The template body with every {{name:type}} placeholder replaced by the
  // value typed for it.
  const previewContent = $derived.by(() => {
    if (!originalTemplate) return "";
    let body: unknown = originalTemplate.attributes?.payload?.body;
    if (isJsonObject(body) && body.content) {
      body = body.content;
    }
    let next = typeof body === "string" ? body : String(body ?? "");
    for (const field of templateFields) {
      const placeholder = `{{${field.name}:${field.type}}}`;
      next = next.replace(placeholder, textOf(field.name));
    }
    return next;
  });

  $effect(() => {
    if (previewContent) {
      content = previewContent;
      onContentChange(previewContent);
    }
  });

  onMount(async () => {
    const response = await getTemplates(space_name);
    templates = recordsOf(response);
    detectAndParseTemplate();
  });

  /** A template entry's text: the `content` of its body. */
  function templateContentOf(template: EntryRecord): string {
    return bodyAs<TemplateBody>(template.attributes?.payload)?.content ?? "";
  }

  // Find the template whose placeholders all have a value in `content`, and
  // seed the form from it.
  function detectAndParseTemplate() {
    if (!content || templates.length === 0) return;

    for (const template of templates) {
      const templateContent = templateContentOf(template);
      const fields = extractFields(templateContent);
      if (fields.length === 0) continue;

      const filledValues = extractValuesFromContent(content, templateContent, fields);
      if (filledValues && Object.keys(filledValues).length === fields.length) {
        originalTemplate = template;
        templateFields = fields;
        fieldValues = filledValues;
        break;
      }
    }
  }

  function extractFields(templateContent: string): TemplateField[] {
    const fieldRegex = /\{\{(\w+):(\w+)\}\}/g;
    const fields: TemplateField[] = [];
    let match;

    while ((match = fieldRegex.exec(templateContent)) !== null) {
      const [, name, type] = match;
      fields.push({ name, type });
    }

    return fields;
  }

  function extractValuesFromContent(
    filledContent: string,
    templateContent: string,
    fields: TemplateField[],
  ): Record<string, FieldValue> | null {
    const values: Record<string, FieldValue> = {};

    const plainContent = String(filledContent).replace(/<[^>]+>/g, "");

    for (const field of fields) {
      const placeholder = `{{${field.name}:${field.type}}}`;

      const templateLine = templateContent
        .split("\n")
        .find((line) => line.includes(placeholder));

      if (!templateLine) continue;

      const prefix = templateLine.split(placeholder)[0].trim();

      const regex = new RegExp(
        prefix.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\s*:?\\s*(.+)",
        "i"
      );

      const match = plainContent.match(regex);
      if (match) {
        const raw = match[1].trim();
        let value: FieldValue = raw;

        if (field.type === "number") {
          value = Number(raw);
        } else if (field.type === "checkbox") {
          value = ["true", "1", "on"].includes(raw.toLowerCase());
        }

        values[field.name] = value;
      } else {
        return null;
      }
    }

    return values;
  }

  function getFieldType(type: string) {
    switch (type) {
      case "string":
        return "text";
      case "int":
      case "float":
        return "number";
      case "number":
        return "number";
      case "date":
        return "date";
      case "text":
        return "textarea";
      case "bool":
      case "checkbox":
        return "checkbox";
      case "list":
      case "object":
      case "list_object":
        return "textarea";
      default:
        return "text";
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
        return ``;
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

  function handleFieldChange(fieldName: string, value: FieldValue) {
    fieldValues = { ...fieldValues, [fieldName]: value };
  }
</script>

{#if originalTemplate && templateFields.length > 0}
  <div class="template-editor">
    <div class="template-info">
      <h4>
        Editing Template: {originalTemplate.shortname || "Template"}
      </h4>
      <p class="template-description">
        Edit the dynamic fields below. The template structure will remain
        unchanged.
      </p>
    </div>

    <div class="template-fields">
      {#each templateFields as field (field.name)}
        <div class="field-group">
          <label for={field.name} class="field-label">
            {field.name} ({field.type})
          </label>
          {#if getFieldType(field.type) === "textarea"}
            <textarea
              id={field.name}
              value={textOf(field.name)}
              oninput={(e) => handleFieldChange(field.name, e.currentTarget.value)}
              class="field-textarea"
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
              onchange={(e) => handleFieldChange(field.name, e.currentTarget.checked)}
              class="field-checkbox"
            />
          {:else}
            <input
              id={field.name}
              type={getFieldType(field.type)}
              value={textOf(field.name)}
              oninput={(e) => handleFieldChange(field.name, e.currentTarget.value)}
              class="field-input"
              placeholder={getFieldPlaceholder(field.type, field.name)}
            />
          {/if}
        </div>
      {/each}
    </div>

    {#if previewContent}
      <div class="template-preview">
        <h5>{$_("labels.preview")}</h5>
        <div class="preview-content">
          {previewContent}
        </div>
      </div>
    {/if}
  </div>
{:else}
  <div class="template-loading">
    <p>{$_("template_generator.loading_editor")}</p>
  </div>
{/if}

<style>
  .template-editor {
    background: var(--color-surface);
    border: 1px solid var(--color-surface-3);
    border-radius: 8px;
    padding: 20px;
  }

  .template-info {
    margin-bottom: 20px;
    padding-bottom: 15px;
    border-bottom: 1px solid var(--color-border);
  }

  .template-info h4 {
    margin: 0 0 8px 0;
    color: var(--color-text-muted);
    font-size: 16px;
    font-weight: 600;
  }

  .template-description {
    margin: 0;
    color: var(--color-text-muted);
    font-size: 14px;
  }

  .template-fields {
    margin-bottom: 20px;
  }

  .field-group {
    margin-bottom: 16px;
  }

  .field-label {
    display: block;
    margin-bottom: 6px;
    font-weight: 500;
    color: var(--color-text-muted);
    font-size: 14px;
    text-transform: capitalize;
  }

  .field-input,
  .field-textarea {
    width: 100%;
    padding: 10px 12px;
    border: 1px solid var(--color-border);
    border-radius: 4px;
    font-size: 14px;
    transition:
      border-color 0.15s ease-in-out,
      box-shadow 0.15s ease-in-out;
    box-sizing: border-box;
  }

  .field-input:focus,
  .field-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 2px rgba(0, 123, 255, 0.25);
  }

  .field-textarea {
    resize: vertical;
    font-family: inherit;
  }

  .field-checkbox {
    transform: scale(1.2);
    margin: 8px 0;
  }

  .field-hint {
    display: block;
    margin-top: 6px;
    font-size: 12px;
    color: var(--color-text-muted);
    font-style: italic;
  }

  .template-preview {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 4px;
    padding: 15px;
  }

  .template-preview h5 {
    margin: 0 0 12px 0;
    color: var(--color-text-muted);
    font-size: 14px;
    font-weight: 600;
  }

  .preview-content {
    background: var(--color-surface);
    border: 1px solid var(--color-surface-3);
    border-radius: 4px;
    padding: 12px;
    white-space: pre-wrap;
    font-family: "Monaco", "Menlo", monospace;
    font-size: 13px;
    line-height: 1.5;
    color: var(--color-text-muted);
  }

  .template-loading {
    text-align: center;
    padding: 40px;
    color: var(--color-text-muted);
  }
</style>
