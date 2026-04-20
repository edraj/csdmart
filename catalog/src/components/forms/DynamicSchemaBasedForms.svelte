<script lang="ts">

  import { _ } from "svelte-i18n";
  import {
    createArrayItemFromSchema,
    getNestedProperty,
    getSchemaPropertyByPath,
    initializeContentFromSchema,
    isPropertyRequired,
    setNestedProperty,
  } from "../../lib/formUtils";
  import type { Schema } from "../../lib/types";

  let {
    content = $bindable({}),
    schema,
    readOnly = false,
  }: {
    content: Record<string, any>;
    schema: Schema;
    readOnly?: boolean;
  } = $props();

  $effect.pre(() => {
    if (schema && schema.properties) {
      const initialized = initializeContentFromSchema(schema.properties, content);
      if (Object.keys(initialized).length > Object.keys(content).length) {
        content = initialized;
      }
    }
  });

  function createEmptyItemFromExisting(existing: any): any {
    if (existing === null || existing === undefined) return '';
    if (Array.isArray(existing)) return [];
    if (typeof existing === 'object') {
      const empty: Record<string, any> = {};
      for (const key of Object.keys(existing)) {
        const val = existing[key];
        if (typeof val === 'boolean') empty[key] = false;
        else if (typeof val === 'number') empty[key] = 0;
        else if (typeof val === 'string') empty[key] = '';
        else if (Array.isArray(val)) empty[key] = [];
        else if (typeof val === 'object' && val !== null) empty[key] = createEmptyItemFromExisting(val);
        else empty[key] = null;
      }
      return empty;
    }
    if (typeof existing === 'boolean') return false;
    if (typeof existing === 'number') return 0;
    return '';
  }

  function addArrayItem(path: any) {
    let target = getNestedProperty(content, path);
    if (!target) {
      target = [];
      setNestedProperty(content, path, target);
    }

    const schemaProp = getSchemaPropertyByPath(schema, path);
    let newItem: any;
    if (schemaProp?.items && schemaProp.items.properties && Object.keys(schemaProp.items.properties).length > 0) {
      newItem = createArrayItemFromSchema(schemaProp.items);
    } else if (schemaProp?.items) {
      newItem = createArrayItemFromSchema(schemaProp.items);
    } else {
      newItem = '';
    }

    // If existing items are objects, ensure the new item has the same keys
    if (target.length > 0 && typeof target[0] === 'object' && target[0] !== null && !Array.isArray(target[0])) {
      const template = createEmptyItemFromExisting(target[0]);
      if (typeof newItem === 'object' && newItem !== null && !Array.isArray(newItem)) {
        newItem = { ...template, ...newItem };
      } else {
        newItem = template;
      }
    }

    target.push(newItem);
    content = { ...content };
  }

  function removeArrayItem(path: any, index: any) {
    let target = content;
    const parts = path.split(".");

    for (let i = 0; i < parts.length - 1; i++) {
      if (!target[parts[i]]) return;
      target = target[parts[i]];
    }

    const lastPart = parts[parts.length - 1];
    if (!target[lastPart]) return;

    target[lastPart].splice(index, 1);

    content = { ...content };
  }

  function isRequired(propertyName: any) {
    return isPropertyRequired(schema, propertyName);
  }
</script>

<div class="form-container">
  {#if schema && schema.properties}
    <div class="form-content">
      {#each Object.keys(schema.properties) as propName}
        {@const property = schema.properties[propName]}
        <div class="field-group">
          <label for={propName} class="field-label">
            {#if isRequired(propName)}
              <span class="required-indicator">*</span>
            {/if}
            {property.title || propName}
          </label>

          {#if property.description}
            <p class="field-description">{property.description}</p>
          {/if}

          {#if property.type === "string"}
            {#if property.format === "date-time" || property.format === "date"}
              <input
                id={propName}
                type="date"
                bind:value={content[propName]}
                required={isRequired(propName)}
                disabled={readOnly}
                class="form-input"
              />
            {:else if property.format === "time"}
              <input
                id={propName}
                type="time"
                bind:value={content[propName]}
                required={isRequired(propName)}
                disabled={readOnly}
                class="form-input"
              />
            {:else if property.format === "email"}
              <input
                id={propName}
                type="email"
                bind:value={content[propName]}
                required={isRequired(propName)}
                disabled={readOnly}
                class="form-input"
              />
            {:else if property.format === "uri"}
              <input
                id={propName}
                type="url"
                bind:value={content[propName]}
                required={isRequired(propName)}
                disabled={readOnly}
                class="form-input"
              />
            {:else if property.enum}
              <select
                id={propName}
                bind:value={content[propName]}
                required={isRequired(propName)}
                disabled={readOnly}
                class="form-select"
              >
                <option value="">{$_("SelectAnOption")}</option>
                {#each property.enum as option}
                  <option value={option}>{option}</option>
                {/each}
              </select>
            {:else if property.maxLength && property.maxLength > 100}
              <textarea
                id={propName}
                rows="4"
                bind:value={content[propName]}
                required={isRequired(propName)}
                disabled={readOnly}
                class="form-textarea"
                placeholder={$_("EnterYourTextHere")}
              ></textarea>
            {:else}
              <input
                id={propName}
                type="text"
                bind:value={content[propName]}
                required={isRequired(propName)}
                minlength={property.minLength}
                maxlength={property.maxLength}
                pattern={property.pattern}
                disabled={readOnly}
                class="form-input"
              />
            {/if}
          {:else if property.type === "number" || property.type === "integer"}
            <input
              id={propName}
              type="number"
              bind:value={content[propName]}
              required={isRequired(propName)}
              min={property.minimum}
              max={property.maximum}
              step={property.type === "integer"
                ? 1
                : property.multipleOf || "any"}
              disabled={readOnly}
              class="form-input"
            />
          {:else if property.type === "boolean"}
            <div class="checkbox-container">
              <input
                id={propName}
                type="checkbox"
                bind:checked={content[propName]}
                disabled={readOnly}
                class="form-checkbox"
              />
              <span class="checkbox-label">
                {content[propName] ? $_("Yes") : $_("No")}
              </span>
            </div>
          {:else if property.type === "array"}
            <div class="array-container">
              <div class="array-header">
                <h3 class="array-title">{property.title || propName}</h3>
                {#if !readOnly}
                  <button
                    type="button"
                    class="btn btn-primary btn-small"
                    onclick={() => addArrayItem(propName)}
                  >
                    <svg
                      class="btn-icon"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                    >
                      <path
                        stroke-linecap="round"
                        stroke-linejoin="round"
                        stroke-width="2"
                        d="M12 4v16m8-8H4"
                      />
                    </svg>
                    {$_("AddItem")}
                  </button>
                {/if}
              </div>

              {#if content[propName] && content[propName].length > 0}
                <div class="array-items">
                  {#each content[propName] as item, index}
                    <div class="array-item">
                      <div class="array-item-content">
                        {#if property.items?.type === "object" && property.items?.properties && Object.keys(property.items.properties).length > 0}
                          <div class="object-fields">
                            {#each Object.keys(property.items!.properties) as itemPropName}
                              {@const itemProperty =
                                property.items!.properties[itemPropName]}
                              <div class="object-field">
                                <label
                                  for={`${propName}-${index}-${itemPropName}`}
                                  class="object-field-label"
                                >
                                  {itemProperty.title || itemPropName}
                                </label>

                                {#if itemProperty.type === "string"}
                                  {#if itemProperty.format === "date-time" || itemProperty.format === "date"}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="date"
                                      value={content[propName][index][itemPropName] ?? ''}
                                      oninput={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.value;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.format === "time"}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="time"
                                      value={content[propName][index][itemPropName] ?? ''}
                                      oninput={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.value;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.format === "email"}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="email"
                                      value={content[propName][index][itemPropName] ?? ''}
                                      oninput={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.value;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.format === "uri"}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="url"
                                      value={content[propName][index][itemPropName] ?? ''}
                                      oninput={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.value;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.enum}
                                    <select
                                      id={`${propName}-${index}-${itemPropName}`}
                                      value={content[propName][index][itemPropName] ?? ''}
                                      onchange={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.value;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-select form-input-small"
                                    >
                                      <option value="">{$_("SelectAnOption")}</option>
                                      {#each itemProperty.enum as option}
                                        <option value={option}>{option}</option>
                                      {/each}
                                    </select>
                                  {:else if itemProperty.maxLength && itemProperty.maxLength > 100}
                                    <textarea
                                      id={`${propName}-${index}-${itemPropName}`}
                                      rows="3"
                                      value={content[propName][index][itemPropName] ?? ''}
                                      oninput={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.value;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-textarea form-input-small"
                                    ></textarea>
                                  {:else}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="text"
                                      value={content[propName][index][itemPropName] ?? ''}
                                      oninput={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.value;
                                        content = { ...content };
                                      }}
                                      minlength={itemProperty.minLength}
                                      maxlength={itemProperty.maxLength}
                                      pattern={itemProperty.pattern}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {/if}
                                {:else if itemProperty.type === "number" || itemProperty.type === "integer"}
                                  <input
                                    id={`${propName}-${index}-${itemPropName}`}
                                    type="number"
                                    value={content[propName][index][itemPropName] ?? ''}
                                    oninput={(e) => {
                                      content[propName][index][itemPropName] = e.currentTarget.valueAsNumber;
                                      content = { ...content };
                                    }}
                                    min={itemProperty.minimum}
                                    max={itemProperty.maximum}
                                    step={itemProperty.type === "integer" ? 1 : itemProperty.multipleOf || "any"}
                                    disabled={readOnly}
                                    class="form-input form-input-small"
                                  />
                                {:else if itemProperty.type === "boolean"}
                                  <div class="checkbox-container">
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="checkbox"
                                      checked={content[propName][index][itemPropName] ?? false}
                                      onchange={(e) => {
                                        content[propName][index][itemPropName] = e.currentTarget.checked;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-checkbox"
                                    />
                                  </div>
                                {/if}
                              </div>
                            {/each}
                          </div>
                        {:else if property.items?.type === "string" || (typeof item === "string")}
                          <input
                            value={content[propName][index] ?? ''}
                            oninput={(e) => {
                              content[propName][index] = e.currentTarget.value;
                              content = { ...content };
                            }}
                            disabled={readOnly}
                            class="form-input"
                          />
                        {:else if property.items?.type === "number" || property.items?.type === "integer" || (typeof item === "number")}
                          <input
                            type="number"
                            value={content[propName][index] ?? ''}
                            oninput={(e) => {
                              content[propName][index] = e.currentTarget.valueAsNumber;
                              content = { ...content };
                            }}
                            disabled={readOnly}
                            class="form-input"
                          />
                        {:else if property.items?.type === "boolean" || (typeof item === "boolean")}
                          <div class="checkbox-container">
                            <input
                              type="checkbox"
                              checked={content[propName][index] ?? false}
                              onchange={(e) => {
                                content[propName][index] = e.currentTarget.checked;
                                content = { ...content };
                              }}
                              disabled={readOnly}
                              class="form-checkbox"
                            />
                          </div>
                        {:else if typeof item === "object" && item !== null}
                          <div class="object-fields">
                            {#each Object.keys(item) as itemKey}
                              <div class="object-field">
                                <label
                                  for={`${propName}-${index}-${itemKey}`}
                                  class="object-field-label"
                                >
                                  {itemKey}
                                </label>
                                {#if typeof item[itemKey] === "boolean"}
                                  <div class="checkbox-container">
                                    <input
                                      id={`${propName}-${index}-${itemKey}`}
                                      type="checkbox"
                                      checked={content[propName][index][itemKey] ?? false}
                                      onchange={(e) => {
                                        content[propName][index][itemKey] = e.currentTarget.checked;
                                        content = { ...content };
                                      }}
                                      disabled={readOnly}
                                      class="form-checkbox"
                                    />
                                  </div>
                                {:else if typeof item[itemKey] === "number"}
                                  <input
                                    id={`${propName}-${index}-${itemKey}`}
                                    type="number"
                                    value={content[propName][index][itemKey] ?? ''}
                                    oninput={(e) => {
                                      content[propName][index][itemKey] = e.currentTarget.valueAsNumber;
                                      content = { ...content };
                                    }}
                                    disabled={readOnly}
                                    class="form-input form-input-small"
                                  />
                                {:else}
                                  <input
                                    id={`${propName}-${index}-${itemKey}`}
                                    type="text"
                                    value={content[propName][index][itemKey] ?? ''}
                                    oninput={(e) => {
                                      content[propName][index][itemKey] = e.currentTarget.value;
                                      content = { ...content };
                                    }}
                                    disabled={readOnly}
                                    class="form-input form-input-small"
                                  />
                                {/if}
                              </div>
                            {/each}
                          </div>
                        {/if}
                      </div>

                      {#if !readOnly}
                        <div class="array-item-actions">
                          <button
                            type="button"
                            class="btn btn-danger btn-small"
                            onclick={() => removeArrayItem(propName, index)}
                          >
                            <svg
                              class="btn-icon"
                              viewBox="0 0 24 24"
                              fill="none"
                              stroke="currentColor"
                            >
                              <path
                                stroke-linecap="round"
                                stroke-linejoin="round"
                                stroke-width="2"
                                d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                              />
                            </svg>
                            {$_("Remove")}
                          </button>
                        </div>
                      {/if}
                    </div>
                  {/each}
                </div>
              {:else}
                <div class="empty-state">
                  <p class="empty-message">{$_("NoItemsAddedYet")}</p>
                </div>
              {/if}
            </div>
          {:else if property.type === "object" && property.properties}
            <div class="object-container">
              <div class="object-header">
                <h3 class="object-title">{property.title || propName}</h3>
              </div>
              <div class="object-content">
                {#each Object.keys(property.properties) as nestedPropName}
                  {@const nestedProperty = property.properties[nestedPropName]}
                  <div class="object-field">
                    <label
                      for={`${propName}-${nestedPropName}`}
                      class="object-field-label"
                    >
                      {nestedProperty.title || nestedPropName}
                    </label>

                    {#if nestedProperty.type === "string"}
                      <input
                        id={`${propName}-${nestedPropName}`}
                        value={content[propName]?.[nestedPropName] ?? ''}
                        oninput={(e) => {
                          if (!content[propName]) content[propName] = {};
                          content[propName][nestedPropName] = e.currentTarget.value;
                        }}
                        disabled={readOnly}
                        class="form-input form-input-small"
                      />
                    {:else if nestedProperty.type === "number" || nestedProperty.type === "integer"}
                      <input
                        id={`${propName}-${nestedPropName}`}
                        type="number"
                        value={content[propName]?.[nestedPropName] ?? ''}
                        oninput={(e) => {
                          if (!content[propName]) content[propName] = {};
                          content[propName][nestedPropName] = e.currentTarget.valueAsNumber;
                        }}
                        disabled={readOnly}
                        class="form-input form-input-small"
                      />
                    {:else if nestedProperty.type === "boolean"}
                      <div class="checkbox-container">
                        <input
                          id={`${propName}-${nestedPropName}`}
                          type="checkbox"
                          checked={content[propName]?.[nestedPropName] ?? false}
                          onchange={(e) => {
                            if (!content[propName]) content[propName] = {};
                            content[propName][nestedPropName] = e.currentTarget.checked;
                          }}
                          disabled={readOnly}
                          class="form-checkbox"
                        />
                      </div>
                    {/if}
                  </div>
                {/each}
              </div>
            </div>
          {/if}
        </div>
      {/each}
    </div>
  {:else}
    <div class="empty-state">
      <p class="empty-message">
        {$_("NoSchemaProvided")}
      </p>
    </div>
  {/if}
</div>

<style>
  .form-container {
    width: 100%;
    padding: 24px 0;
  }

  .form-content {
    display: flex;
    flex-direction: column;
    gap: 24px;
  }

  .field-group {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  .field-label {
    font-size: 14px;
    font-weight: 600;
    color: #374151;
    display: flex;
    align-items: center;
    gap: 4px;
    margin-bottom: 4px;
  }

  .required-indicator {
    color: #ef4444;
    font-size: 16px;
    font-weight: 700;
  }

  .field-description {
    font-size: 12px;
    color: #6b7280;
    margin: 0 0 8px 0;
    line-height: 1.4;
  }

  .form-input,
  .form-select,
  .form-textarea {
    width: 100%;
    padding: 12px 16px;
    border: 2px solid #e5e7eb;
    border-radius: 8px;
    font-size: 14px;
    transition: all 0.2s ease;
    background: #ffffff;
    color: #374151;
    box-sizing: border-box;
  }

  .form-input:focus,
  .form-select:focus,
  .form-textarea:focus {
    outline: none;
    border-color: #3b82f6;
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .form-input:hover,
  .form-select:hover,
  .form-textarea:hover {
    border-color: #d1d5db;
  }

  .form-input-small {
    padding: 8px 12px;
    font-size: 13px;
  }

  .form-textarea {
    resize: vertical;
    min-height: 100px;
    font-family: inherit;
  }

  .checkbox-container {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 8px 0;
  }

  .form-checkbox {
    width: 18px;
    height: 18px;
    border: 2px solid #d1d5db;
    border-radius: 4px;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .form-checkbox:checked {
    background-color: #3b82f6;
    border-color: #3b82f6;
  }

  .checkbox-label {
    font-size: 14px;
    color: #6b7280;
    font-weight: 500;
  }

  .array-container {
    border: 2px solid #f1f5f9;
    border-radius: 12px;
    padding: 20px;
    background: #fafbfc;
  }

  .array-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 16px;
    padding-bottom: 12px;
    border-bottom: 1px solid #e5e7eb;
  }

  .array-title {
    font-size: 16px;
    font-weight: 600;
    color: #374151;
    margin: 0;
  }

  .array-items {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .array-item {
    background: #ffffff;
    border: 1px solid #e5e7eb;
    border-radius: 8px;
    padding: 16px;
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 16px;
    transition: all 0.2s ease;
  }

  .array-item:hover {
    border-color: #d1d5db;
    box-shadow: 0 2px 4px rgba(0, 0, 0, 0.05);
  }

  .array-item-content {
    flex: 1;
  }

  .array-item-actions {
    flex-shrink: 0;
  }

  .object-container {
    border: 2px solid #f1f5f9;
    border-radius: 12px;
    overflow: hidden;
    background: #fafbfc;
  }

  .object-header {
    background: linear-gradient(135deg, #f8fafc, #f1f5f9);
    padding: 16px 20px;
    border-bottom: 1px solid #e5e7eb;
  }

  .object-title {
    font-size: 16px;
    font-weight: 600;
    color: #374151;
    margin: 0;
  }

  .object-content {
    padding: 20px;
    background: #ffffff;
  }

  .object-fields {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
    gap: 16px;
  }

  .object-field {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  .object-field-label {
    font-size: 13px;
    font-weight: 600;
    color: #4b5563;
    margin-bottom: 4px;
  }

  .btn {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 10px 16px;
    border: none;
    border-radius: 8px;
    font-size: 14px;
    font-weight: 600;
    cursor: pointer;
    transition: all 0.2s ease;
    text-decoration: none;
    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  }

  .btn:hover {
    transform: translateY(-1px);
    box-shadow: 0 4px 8px rgba(0, 0, 0, 0.15);
  }

  .btn-small {
    padding: 6px 12px;
    font-size: 12px;
  }

  .btn-primary {
    background: linear-gradient(135deg, #3b82f6, #2563eb);
    color: #ffffff;
  }

  .btn-primary:hover {
    background: linear-gradient(135deg, #2563eb, #1d4ed8);
  }

  .btn-danger {
    background: linear-gradient(135deg, #ef4444, #dc2626);
    color: #ffffff;
  }

  .btn-danger:hover {
    background: linear-gradient(135deg, #dc2626, #b91c1c);
  }

  .btn-icon {
    width: 16px;
    height: 16px;
    stroke-width: 2;
  }

  .empty-state {
    text-align: center;
    padding: 48px 24px;
    background: #f8fafc;
    border-radius: 12px;
    border: 2px dashed #cbd5e1;
  }

  .empty-message {
    font-size: 16px;
    color: #64748b;
    margin: 0;
    font-weight: 500;
  }

  @media (max-width: 640px) {
    .form-container {
      padding: 16px;
    }

    .array-header {
      flex-direction: column;
      align-items: stretch;
      gap: 12px;
    }

    .array-item {
      flex-direction: column;
      gap: 12px;
    }

    .object-fields {
      grid-template-columns: 1fr;
    }

    .btn {
      width: 100%;
      justify-content: center;
    }
  }
</style>
