<script lang="ts">
  import { untrack } from "svelte";
  import { _ } from "@/i18n";
  import { confirm } from "@/lib/confirm";

  const {
    content = {},
    onContentChange = () => {},
  }: { content?: any; isEditMode?: boolean; onContentChange?: (data: Record<string, any>) => void } = $props();

  let jsonData: Record<string, any> = $state({});
  let fieldTypes: Record<string, any> = $state({});
  
  // Modal state for adding new field
  let showAddFieldModal = $state(false);
  let newFieldName = $state("");
  let newFieldType = $state("string");
  let newFieldValue = $state("");
  let newFieldError = $state("");
  
  const fieldTypeOptions = [
    { value: "string", name: "String" },
    { value: "integer", name: "Integer" },
    { value: "number", name: "Number" },
    { value: "boolean", name: "Boolean" },
    { value: "array", name: "Array" },
    { value: "object", name: "Object" }
  ];

  $effect(() => {
    if (content && typeof content === "object") {
      untrack(() => {
        jsonData = { ...content };
        detectFieldTypes();
      });
    } else if (typeof content === "string") {
      try {
        untrack(() => {
          jsonData = JSON.parse(content);
          detectFieldTypes();
        });
      } catch {
        untrack(() => {
          jsonData = {};
          fieldTypes = {};
        });
      }
    }
  });

  function detectFieldTypes() {
    const types: Record<string, any> = {};
    for (const [key, value] of Object.entries(jsonData)) {
      if (typeof value === "number") {
        types[key] = Number.isInteger(value) ? "integer" : "number";
      } else if (typeof value === "boolean") {
        types[key] = "boolean";
      } else if (Array.isArray(value)) {
        types[key] = "array";
      } else if (typeof value === "object" && value !== null) {
        types[key] = "object";
      } else {
        types[key] = "string";
      }
    }
    fieldTypes = types;
  }

  function handleFieldChange(key: any, value: any, type: any) {
    let processedValue: any;

    switch (type) {
      case "integer":
        processedValue = parseInt(value) || 0;
        break;
      case "number":
        processedValue = parseFloat(value) || 0;
        break;
      case "boolean":
        processedValue = Boolean(value);
        break;
      case "array":
        try {
          processedValue =
            typeof value === "string"
              ? value
                  .split(",")
                  .map((item) => item.trim())
                  .filter((item) => item)
              : value;
        } catch (_e) {
          processedValue = [];
        }
        break;
      case "object":
        try {
          processedValue =
            typeof value === "string" ? JSON.parse(value) : value;
        } catch (_e) {
          processedValue = {};
        }
        break;
      default:
        processedValue = String(value);
    }

    jsonData[key] = processedValue;

    onContentChange(jsonData);
  }

  function openAddFieldModal() {
    newFieldName = "";
    newFieldType = "string";
    newFieldValue = "";
    newFieldError = "";
    showAddFieldModal = true;
  }
  
  function closeAddFieldModal() {
    showAddFieldModal = false;
  }
  
  function processNewFieldValue(value: any, type: any) {
    switch (type) {
      case "integer":
        return parseInt(value) || 0;
      case "number":
        return parseFloat(value) || 0;
      case "boolean":
        return value === "true" || value === true;
      case "array":
        try {
          return JSON.parse(value);
        } catch {
          return value.split(",").map((item: any) => item.trim()).filter((item: any) => item);
        }
      case "object":
        try {
          return JSON.parse(value);
        } catch {
          return {};
        }
      default:
        return String(value);
    }
  }
  
  function handleAddNewField() {
    newFieldError = "";
    
    if (!newFieldName.trim()) {
      newFieldError = $_("json_editor.field_name_required") || "Field name is required";
      return;
    }
    
    if (Object.hasOwn(jsonData, newFieldName)) {
      newFieldError = $_("json_editor.field_exists") || "Field already exists";
      return;
    }
    
    const processedValue = processNewFieldValue(newFieldValue, newFieldType);
    
    jsonData[newFieldName] = processedValue;
    fieldTypes[newFieldName] = newFieldType;
    
    onContentChange(jsonData);
    closeAddFieldModal();
  }
  
  function handleTypeChange() {
    // Reset value when type changes to avoid confusion
    newFieldValue = "";
  }

  async function removeField(key: string) {
    const confirmed = await confirm({
      title: $_("json_editor.remove_field_title", { values: { key } }),
      body: $_("json_editor.remove_field_confirm"),
      variant: "danger",
      confirmLabel: $_("ui.remove"),
    });
    if (!confirmed) return;
    delete jsonData[key];
    delete fieldTypes[key];
    jsonData = { ...jsonData };
    fieldTypes = { ...fieldTypes };
    onContentChange(jsonData);
  }

  function changeFieldType(key: any, newType: any) {
    fieldTypes[key] = newType;

    const currentValue = jsonData[key];
    let convertedValue;

    switch (newType) {
      case "integer":
        convertedValue = parseInt(currentValue) || 0;
        break;
      case "number":
        convertedValue = parseFloat(currentValue) || 0;
        break;
      case "boolean":
        convertedValue = Boolean(currentValue);
        break;
      case "array":
        convertedValue = Array.isArray(currentValue) ? currentValue : [];
        break;
      case "object":
        convertedValue =
          typeof currentValue === "object" && !Array.isArray(currentValue)
            ? currentValue
            : {};
        break;
      default:
        convertedValue = String(currentValue);
    }

    jsonData[key] = convertedValue;
    onContentChange(jsonData);
  }

  function formatArrayValue(value: any) {
    return Array.isArray(value) ? value.join(", ") : "";
  }

  function formatObjectValue(value: any) {
    return typeof value === "object" ? JSON.stringify(value, null, 2) : "";
  }
</script>

<div class="json-editor">
  <div class="editor-header">
    <h4 class="editor-title">
      <svg
        class="title-icon"
        fill="none"
        stroke="currentColor"
        viewBox="0 0 24 24"
      >
        <path
          stroke-linecap="round"
          stroke-linejoin="round"
          stroke-width="2"
          d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
        />
      </svg>
      JSON Editor
    </h4>
    <button type="button" class="add-field-btn" onclick={openAddFieldModal}>
      <svg
        class="btn-icon"
        fill="none"
        stroke="currentColor"
        viewBox="0 0 24 24"
      >
        <path
          stroke-linecap="round"
          stroke-linejoin="round"
          stroke-width="2"
          d="M12 4v16m8-8H4"
        />
      </svg>
      Add Field
    </button>
  </div>

  <div class="fields-container">
    {#each Object.entries(jsonData) as [key, value] (key)}
      <div class="field-row">
        <div class="field-header">
          <label class="field-label" for="field-{key}">{key}</label>
          <div class="field-controls">
            <select
              class="type-select"
              bind:value={fieldTypes[key]}
              onchange={(e: any) => changeFieldType(key, (e.target as HTMLInputElement).value)}
            >
              <option value="string">String</option>
              <option value="integer">Integer</option>
              <option value="number">Number</option>
              <option value="boolean">Boolean</option>
              <option value="array">Array</option>
              <option value="object">Object</option>
            </select>
            <button
              type="button"
              class="remove-field-btn"
              onclick={() => removeField(key)}
              title={$_("json_editor.remove_field")}
              aria-label={$_("json_editor.remove_field")}
            >
              <svg
                class="btn-icon"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M6 18L18 6M6 6l12 12"
                />
              </svg>
            </button>
          </div>
        </div>

        <div class="field-input">
          {#if fieldTypes[key] === "boolean"}
            <label class="checkbox-container">
              <input
                type="checkbox"
                bind:checked={jsonData[key]}
                onchange={(e: any) =>
                  handleFieldChange(key, (e.target as HTMLInputElement).checked, "boolean")}
              />
              <span class="checkbox-label">{value ? "True" : "False"}</span>
            </label>
          {:else if fieldTypes[key] === "integer"}
            <input
              type="number"
              step="1"
              bind:value={jsonData[key]}
              onchange={(e: any) =>
                handleFieldChange(key, (e.target as HTMLInputElement).value, "integer")}
              class="field-input-element"
            />
          {:else if fieldTypes[key] === "number"}
            <input
              type="number"
              step="any"
              bind:value={jsonData[key]}
              onchange={(e: any) => handleFieldChange(key, (e.target as HTMLInputElement).value, "number")}
              class="field-input-element"
            />
          {:else if fieldTypes[key] === "array"}
            <input
              type="text"
              value={formatArrayValue(value)}
              onchange={(e: any) => handleFieldChange(key, (e.target as HTMLInputElement).value, "array")}
              class="field-input-element"
              placeholder={$_("json_editor.csv_placeholder")}
            />
          {:else if fieldTypes[key] === "object"}
            <textarea
              value={formatObjectValue(value)}
              onchange={(e: any) => handleFieldChange(key, (e.target as HTMLInputElement).value, "object")}
              class="field-textarea"
              placeholder={$_("json_editor.object_placeholder")}
              rows="3"
            ></textarea>
          {:else}
            <input
              type="text"
              bind:value={jsonData[key]}
              onchange={(e: any) => handleFieldChange(key, (e.target as HTMLInputElement).value, "string")}
              class="field-input-element"
            />
          {/if}
        </div>
      </div>
    {/each}

    {#if Object.keys(jsonData).length === 0}
      <div class="empty-state">
        <svg
          class="empty-icon"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            stroke-linecap="round"
            stroke-linejoin="round"
            stroke-width="2"
            d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
          />
        </svg>
        <p class="empty-text">{$_("json_editor.no_fields")}</p>
        <button type="button" class="add-first-field-btn" onclick={openAddFieldModal}>
          Add your first field
        </button>
      </div>
    {/if}
  </div>
</div>

<!-- Add Field Modal -->
{#if showAddFieldModal}
  <div class="modal-overlay" role="presentation" onclick={(e) => { if(e.target === e.currentTarget) closeAddFieldModal(); }}>
    <div class="modal-container">
      <!-- Header -->
      <div class="modal-header">
        <h3 class="modal-title">
          {$_("json_editor.add_field_title") || "Add New Field"}
        </h3>
        <button class="modal-close-btn" onclick={closeAddFieldModal} aria-label={$_("common.close")}>
          <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </div>
      
      <!-- Body -->
      <div class="modal-body">
        <!-- Field Name -->
        <div class="form-field">
          <label for="field-name" class="field-label">
            {$_("json_editor.field_name_label") || "Field Name"}
          </label>
          <input
            id="field-name"
            type="text"
            bind:value={newFieldName}
            placeholder={$_("json_editor.field_name_placeholder") || "Enter field name"}
            class="field-input"
          />
        </div>
        
        <!-- Field Type -->
        <div class="form-field">
          <label for="field-type" class="field-label">
            {$_("json_editor.field_type_label") || "Field Type"}
          </label>
          <div class="custom-select-wrapper">
            <select
              id="field-type"
              bind:value={newFieldType}
              onchange={handleTypeChange}
              class="custom-select"
            >
              {#each fieldTypeOptions as option (option.value)}
                <option value={option.value}>{option.name}</option>
              {/each}
            </select>
            <svg class="select-arrow" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
            </svg>
          </div>
        </div>
        
        <!-- Field Value -->
        <div class="form-field">
          <label for="field-value" class="field-label">
            {$_("json_editor.field_value_label") || "Initial Value"}
            <span class="value-hint">
              {newFieldType === "array" ? "(JSON array or comma-separated)" : newFieldType === "object" ? "(JSON object)" : ""}
            </span>
          </label>
          {#if newFieldType === "boolean"}
            <div class="custom-select-wrapper">
              <select
                id="field-value"
                bind:value={newFieldValue}
                class="custom-select"
              >
                <option value="true">True</option>
                <option value="false">False</option>
              </select>
              <svg class="select-arrow" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
              </svg>
            </div>
          {:else if newFieldType === "object" || newFieldType === "array"}
            <textarea
              id="field-value"
              bind:value={newFieldValue}
              placeholder={newFieldType === "array" ? '["item1", "item2"]' : '{"key": "value"}'}
              rows="3"
              class="field-textarea"
            ></textarea>
          {:else}
            <input
              id="field-value"
              type={newFieldType === "integer" || newFieldType === "number" ? "number" : "text"}
              bind:value={newFieldValue}
              placeholder={newFieldType === "integer" ? "0" : newFieldType === "number" ? "0.0" : "Enter value"}
              class="field-input"
            />
          {/if}
        </div>
        
        <!-- Error Message -->
        {#if newFieldError}
          <div class="error-message">
            {newFieldError}
          </div>
        {/if}
      </div>
      
      <!-- Footer -->
      <div class="modal-footer">
        <button class="btn btn-cancel" onclick={closeAddFieldModal}>
          {$_("common.cancel") || "Cancel"}
        </button>
        <button class="btn btn-add" onclick={handleAddNewField}>
          {$_("json_editor.add_field_button") || "Add Field"}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .json-editor {
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: 12px;
    padding: 20px;
    min-height: 300px;
  }

  .editor-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 20px;
    padding-bottom: 12px;
    border-bottom: 1px solid var(--color-border);
  }

  .editor-title {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0;
    font-size: 16px;
    font-weight: 600;
    color: var(--color-text);
  }

  .title-icon {
    width: 20px;
    height: 20px;
    color: var(--color-text-muted);
  }

  .add-field-btn {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 14px;
    background: var(--color-primary);
    color: white;
    border: none;
    border-radius: 8px;
    font-size: 14px;
    font-weight: 500;
    cursor: pointer;
    transition: background-color 0.2s;
  }

  .add-field-btn:hover {
    background: var(--color-primary-hover);
  }

  .btn-icon {
    width: 16px;
    height: 16px;
  }

  .fields-container {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .field-row {
    padding: 16px;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    background: var(--color-surface);
  }

  .field-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 10px;
  }

  .field-label {
    font-weight: 500;
    color: var(--color-text);
    font-size: 14px;
  }

  .field-controls {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .type-select {
    padding: 4px 8px;
    border: 1px solid var(--color-border-strong);
    border-radius: 6px;
    font-size: 12px;
    background: white;
    color: var(--color-text);
  }

  .remove-field-btn {
    padding: 4px;
    background: var(--color-danger);
    color: white;
    border: none;
    border-radius: 4px;
    cursor: pointer;
    transition: background-color 0.2s;
  }

  .remove-field-btn:hover {
    background: var(--color-danger);
  }

  .field-input {
    width: 100%;
  }

  .field-input-element {
    width: 100%;
    padding: 10px 12px;
    border: 1px solid var(--color-border-strong);
    border-radius: 6px;
    font-size: 14px;
    transition: border-color 0.2s;
  }

  .field-input-element:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .field-textarea {
    width: 100%;
    padding: 10px 12px;
    border: 1px solid var(--color-border-strong);
    border-radius: 6px;
    font-size: 14px;
    font-family: "Monaco", "Menlo", "Ubuntu Mono", monospace;
    resize: vertical;
    transition: border-color 0.2s;
  }

  .field-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .checkbox-container {
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
  }

  .checkbox-container input[type="checkbox"] {
    width: 16px;
    height: 16px;
  }

  .checkbox-label {
    font-size: 14px;
    color: var(--color-text);
  }

  .empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 40px 20px;
    text-align: center;
  }

  .empty-icon {
    width: 48px;
    height: 48px;
    color: var(--color-text-faint);
    margin-bottom: 12px;
  }

  .empty-text {
    color: var(--color-text-muted);
    font-size: 16px;
    margin: 0 0 16px 0;
  }

  .add-first-field-btn {
    padding: 10px 20px;
    background: var(--color-primary);
    color: white;
    border: none;
    border-radius: 8px;
    font-size: 14px;
    font-weight: 500;
    cursor: pointer;
    transition: background-color 0.2s;
  }

  .add-first-field-btn:hover {
    background: var(--color-primary-hover);
  }

  /* Modal Styles */
  .modal-overlay {
    position: fixed;
    top: 0;
    inset-inline-start: 0;
    inset-inline-end: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 50;
    padding: 16px;
  }

  .modal-container {
    background: var(--color-surface-2);
    border-radius: 12px;
    width: 100%;
    max-width: 480px;
    box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.1), 0 10px 10px -5px rgba(0, 0, 0, 0.04);
    overflow: hidden;
  }

  .modal-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 20px 24px;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-surface-2);
  }

  .modal-title {
    font-size: 18px;
    font-weight: 600;
    color: var(--color-text);
    margin: 0;
  }

  .modal-close-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 32px;
    height: 32px;
    border: none;
    background: var(--color-surface-3);
    color: var(--color-text-muted);
    border-radius: 8px;
    cursor: pointer;
    transition: all 0.2s;
  }

  .modal-close-btn:hover {
    background: var(--color-border);
    color: var(--color-text);
  }

  .modal-body {
    padding: 24px;
    background: var(--color-surface-2);
  }

  .form-field {
    margin-bottom: 20px;
  }

  .form-field:last-child {
    margin-bottom: 0;
  }

  .field-label {
    display: block;
    font-size: 14px;
    font-weight: 500;
    color: var(--color-text);
    margin-bottom: 8px;
  }

  .value-hint {
    font-size: 12px;
    font-weight: 400;
    color: var(--color-text-muted);
    margin-inline-start: 4px;
  }

  .field-input {
    width: 100%;
    padding: 10px 14px;
    border: 1px solid var(--color-border-strong);
    border-radius: 8px;
    font-size: 14px;
    color: var(--color-text);
    background: var(--color-surface-2);
    transition: border-color 0.2s, box-shadow 0.2s;
  }

  .field-input:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .field-textarea {
    width: 100%;
    padding: 10px 14px;
    border: 1px solid var(--color-border-strong);
    border-radius: 8px;
    font-size: 14px;
    color: var(--color-text);
    background: var(--color-surface-2);
    font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
    resize: vertical;
    transition: border-color 0.2s, box-shadow 0.2s;
  }

  .field-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  /* Custom Select Styling */
  .custom-select-wrapper {
    position: relative;
  }

  .custom-select {
    width: 100%;
    padding: 10px 40px 10px 14px;
    border: 1px solid var(--color-border-strong);
    border-radius: 8px;
    font-size: 14px;
    color: var(--color-text);
    background: var(--color-surface-2);
    cursor: pointer;
    appearance: none;
    -webkit-appearance: none;
    -moz-appearance: none;
    transition: border-color 0.2s, box-shadow 0.2s;
  }

  .custom-select:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .select-arrow {
    position: absolute;
    inset-inline-end: 12px;
    top: 50%;
    transform: translateY(-50%);
    width: 16px;
    height: 16px;
    color: var(--color-text-muted);
    pointer-events: none;
  }

  .error-message {
    padding: 10px 14px;
    background: var(--color-danger-soft);
    border: 1px solid var(--color-danger-soft);
    border-radius: 8px;
    color: var(--color-danger);
    font-size: 13px;
    font-weight: 500;
  }

  /* Modal Footer */
  .modal-footer {
    display: flex;
    justify-content: flex-end;
    gap: 12px;
    padding: 16px 24px;
    border-top: 1px solid var(--color-border);
    background: var(--color-surface);
  }

  .btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 10px 20px;
    font-size: 14px;
    font-weight: 500;
    border-radius: 8px;
    cursor: pointer;
    transition: all 0.2s;
    border: none;
  }

  .btn-cancel {
    background: var(--color-surface-2);
    color: var(--color-text);
    border: 1px solid var(--color-border-strong);
  }

  .btn-cancel:hover {
    background: var(--color-surface);
    border-color: var(--color-text-faint);
  }

  .btn-add {
    background: var(--color-primary);
    color: var(--color-surface-2);
  }

  .btn-add:hover {
    background: var(--color-primary-hover);
  }
</style>
