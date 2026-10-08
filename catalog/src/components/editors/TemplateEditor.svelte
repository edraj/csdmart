<script lang="ts">
  import { onMount } from "svelte";
  import { getTemplates } from "@/lib/dmart_services";

  let {
    content = $bindable(""),
    space_name = "",
    onContentChange = () => {},
  }: {
    content?: any;
    space_name?: string;
    onContentChange?: (content: string) => void;
  } = $props();

  let templates: any[] = $state([]);
  let originalTemplate: any = $state(null);
  let templateFields: any[] = $state([]);
  let fieldValues: Record<string, any> = $state({});

  // The template body with every {{name:type}} placeholder replaced by the
  // value typed for it.
  const previewContent = $derived.by(() => {
    if (!originalTemplate) return "";
    let next = originalTemplate?.attributes?.payload?.body;
    if (typeof next === "object" && next?.content) {
      next = next.content;
    }
    if (typeof next !== "string") {
      next = String(next ?? "");
    }
    for (const field of templateFields) {
      const placeholder = `{{${field.name}:${field.type}}}`;
      next = next.replace(placeholder, fieldValues[field.name] || "");
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
    templates = response.records;
    detectAndParseTemplate();
  });

  // Find the template whose placeholders all have a value in `content`, and
  // seed the form from it.
  function detectAndParseTemplate() {
    if (!content || templates.length === 0) return;

    for (const template of templates) {
      const templateContent = template?.attributes?.payload?.body.content;
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

  function extractFields(templateContent: any) {
    const fieldRegex = /\{\{(\w+):(\w+)\}\}/g;
    const fields = [];
    let match;

    while ((match = fieldRegex.exec(templateContent)) !== null) {
      const [, name, type] = match;
      fields.push({ name, type });
    }

    return fields;
  }

  function extractValuesFromContent(filledContent: any, templateContent: any, fields: any) {
    const values: Record<string, any> = {};

    const plainContent = String(filledContent).replace(/<[^>]+>/g, "");

    for (const field of fields) {
      const placeholder = `{{${field.name}:${field.type}}}`;

      const templateLine = templateContent
        .split("\n")
        .find((line: any) => line.includes(placeholder));

      if (!templateLine) continue;

      const prefix = templateLine.split(placeholder)[0].trim();

      const regex = new RegExp(
        prefix.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\s*:?\\s*(.+)",
        "i"
      );

      const match = plainContent.match(regex);
      if (match) {
        let value: any = match[1].trim();

        if (field.type === "number") {
          value = Number(value);
        } else if (field.type === "checkbox") {
          value = ["true", "1", "on"].includes(value.toLowerCase());
        }

        values[field.name] = value;
      } else {
        return null;
      }
    }

    return values;
  }

  function getFieldType(type: any) {
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

  function getFieldPlaceholder(type: any, name: any) {
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

  function handleFieldChange(fieldName: any, value: any) {
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
              value={fieldValues[field.name] || ""}
              oninput={(e) => handleFieldChange(field.name, (e.target as HTMLInputElement).value)}
              class="field-textarea"
              placeholder={getFieldPlaceholder(field.type, field.name)}
              rows={field.type === "list" || field.type === "object" || field.type === "list_object" ? 5 : 3}
            ></textarea>
            {#if field.type === "list"}
              <small class="field-hint">Enter values separated by commas</small>
            {:else if field.type === "object"}
              <small class="field-hint">Enter valid JSON object</small>
            {:else if field.type === "list_object"}
              <small class="field-hint">Enter valid JSON array of objects</small>
            {/if}
          {:else if getFieldType(field.type) === "checkbox"}
            <input
              id={field.name}
              type="checkbox"
              checked={fieldValues[field.name] || false}
              onchange={(e) => handleFieldChange(field.name, (e.target as HTMLInputElement).checked)}
              class="field-checkbox"
            />
          {:else}
            <input
              id={field.name}
              type={getFieldType(field.type)}
              value={fieldValues[field.name] || ""}
              oninput={(e) => handleFieldChange(field.name, (e.target as HTMLInputElement).value)}
              class="field-input"
              placeholder={getFieldPlaceholder(field.type, field.name)}
            />
          {/if}
        </div>
      {/each}
    </div>

    {#if previewContent}
      <div class="template-preview">
        <h5>Preview</h5>
        <div class="preview-content">
          {previewContent}
        </div>
      </div>
    {/if}
  </div>
{:else}
  <div class="template-loading">
    <p>Loading template editor...</p>
  </div>
{/if}

<style>
  .template-editor {
    background: #f8f9fa;
    border: 1px solid #e9ecef;
    border-radius: 8px;
    padding: 20px;
  }

  .template-info {
    margin-bottom: 20px;
    padding-bottom: 15px;
    border-bottom: 1px solid #dee2e6;
  }

  .template-info h4 {
    margin: 0 0 8px 0;
    color: #495057;
    font-size: 16px;
    font-weight: 600;
  }

  .template-description {
    margin: 0;
    color: #6c757d;
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
    color: #495057;
    font-size: 14px;
    text-transform: capitalize;
  }

  .field-input,
  .field-textarea {
    width: 100%;
    padding: 10px 12px;
    border: 1px solid #ced4da;
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
    border-color: #007bff;
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
    color: #6c757d;
    font-style: italic;
  }

  .template-preview {
    background: white;
    border: 1px solid #dee2e6;
    border-radius: 4px;
    padding: 15px;
  }

  .template-preview h5 {
    margin: 0 0 12px 0;
    color: #495057;
    font-size: 14px;
    font-weight: 600;
  }

  .preview-content {
    background: #f8f9fa;
    border: 1px solid #e9ecef;
    border-radius: 4px;
    padding: 12px;
    white-space: pre-wrap;
    font-family: "Monaco", "Menlo", monospace;
    font-size: 13px;
    line-height: 1.5;
    color: #495057;
  }

  .template-loading {
    text-align: center;
    padding: 40px;
    color: #6c757d;
  }
</style>
