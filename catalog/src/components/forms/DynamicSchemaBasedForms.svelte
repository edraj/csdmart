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
  import { isJsonObject, type JsonObject, type Schema } from "../../lib/types";
  import { permissions } from "@/stores/permissions";
  import { constrainEnumOptions, isFieldRestricted } from "@/lib/access-fields";

  interface Props {
    /** The form's data bag: one entry per schema property. */
    content: JsonObject;
    schema: Schema;
    readOnly?: boolean;
    space?: string;
    subpath?: string;
    resourceType?: string;
  }

  let {
    content = $bindable({}),
    schema,
    readOnly = false,
    space = "",
    subpath = "",
    resourceType = "",
  }: Props = $props();

  $effect.pre(() => {
    if (schema && schema.properties) {
      const initialized = initializeContentFromSchema(schema.properties, content);
      if (Object.keys(initialized).length > Object.keys(content).length) {
        content = initialized;
      }
    }
  });

  /** An item shaped like `existing`, with every field reset to its empty value. */
  function createEmptyItemFromExisting(existing: unknown): unknown {
    if (existing === null || existing === undefined) return '';
    if (Array.isArray(existing)) return [];
    if (isJsonObject(existing)) {
      const empty: JsonObject = {};
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

  // --- Typed views over the content bag ------------------------------------
  // The form is schema-driven, so the bag's values are only known at runtime;
  // each field's markup reads its value through the view that matches the
  // field's shape, and writes through the matching setter.

  /** The array under `name`, or [] when the value is not one. */
  function listAt(name: string): unknown[] {
    const value = content[name];
    return Array.isArray(value) ? value : [];
  }

  /** The object at `index` of the array under `name`, or {} when it is not one. */
  function itemAt(name: string, index: number): JsonObject {
    const item = listAt(name)[index];
    return isJsonObject(item) ? item : {};
  }

  /** The object under `name`, or {} when the value is not one. */
  function objectAt(name: string): JsonObject {
    const value = content[name];
    return isJsonObject(value) ? value : {};
  }

  function setListItem(name: string, index: number, value: unknown) {
    const list = content[name];
    if (Array.isArray(list)) list[index] = value;
    content = { ...content };
  }

  function setItemField(name: string, index: number, key: string, value: unknown) {
    const item = listAt(name)[index];
    if (isJsonObject(item)) item[key] = value;
    content = { ...content };
  }

  function setObjectField(name: string, key: string, value: unknown) {
    const existing = content[name];
    const target: JsonObject = isJsonObject(existing) ? existing : {};
    if (target !== existing) content[name] = target;
    target[key] = value;
  }

  function addArrayItem(path: string) {
    const existing = getNestedProperty(content, path);
    let target: unknown[];
    if (Array.isArray(existing)) {
      target = existing;
    } else {
      target = [];
      setNestedProperty(content, path, target);
    }

    const schemaProp = getSchemaPropertyByPath(schema, path);
    let newItem: unknown = schemaProp?.items ? createArrayItemFromSchema(schemaProp.items) : '';

    // If existing items are objects, ensure the new item has the same keys
    const first = target[0];
    if (target.length > 0 && isJsonObject(first)) {
      const template = createEmptyItemFromExisting(first);
      if (isJsonObject(newItem) && isJsonObject(template)) {
        newItem = { ...template, ...newItem };
      } else {
        newItem = template;
      }
    }

    target.push(newItem);
    content = { ...content };
  }

  function removeArrayItem(path: string, index: number) {
    const parts = path.split(".");
    const lastPart = parts[parts.length - 1];
    const parent = parts.length > 1 ? getNestedProperty(content, parts.slice(0, -1).join(".")) : content;
    if (!isJsonObject(parent)) return;

    const list = parent[lastPart];
    if (!Array.isArray(list)) return;

    list.splice(index, 1);

    content = { ...content };
  }

  function isRequired(propertyName: string) {
    return isPropertyRequired(schema, propertyName);
  }
</script>

<div class="form-container">
  {#if schema && schema.properties}
    <div class="form-content">
      {#each Object.keys(schema.properties) as propName (propName)}
        {@const property = schema.properties[propName]}
        {#if !isFieldRestricted($permissions, propName, space, subpath, resourceType)}
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
                {#each constrainEnumOptions(property.enum, $permissions, propName, space, subpath, resourceType, content[propName]) as option (option)}
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
                checked={content[propName] === true}
                onchange={(e) => {
                  content[propName] = e.currentTarget.checked;
                }}
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

              {#if listAt(propName).length > 0}
                <div class="array-items">
                  {#each listAt(propName) as item, index (index)}
                    <div class="array-item">
                      <div class="array-item-content">
                        {#if property.items?.type === "object" && property.items?.properties && Object.keys(property.items.properties).length > 0}
                          <div class="object-fields">
                            {#each Object.keys(property.items!.properties) as itemPropName (itemPropName)}
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
                                      value={itemAt(propName, index)[itemPropName] ?? ''}
                                      oninput={(e) => setItemField(propName, index, itemPropName, e.currentTarget.value)}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.format === "time"}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="time"
                                      value={itemAt(propName, index)[itemPropName] ?? ''}
                                      oninput={(e) => setItemField(propName, index, itemPropName, e.currentTarget.value)}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.format === "email"}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="email"
                                      value={itemAt(propName, index)[itemPropName] ?? ''}
                                      oninput={(e) => setItemField(propName, index, itemPropName, e.currentTarget.value)}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.format === "uri"}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="url"
                                      value={itemAt(propName, index)[itemPropName] ?? ''}
                                      oninput={(e) => setItemField(propName, index, itemPropName, e.currentTarget.value)}
                                      disabled={readOnly}
                                      class="form-input form-input-small"
                                    />
                                  {:else if itemProperty.enum}
                                    <select
                                      id={`${propName}-${index}-${itemPropName}`}
                                      value={String(itemAt(propName, index)[itemPropName] ?? '')}
                                      onchange={(e) => setItemField(propName, index, itemPropName, e.currentTarget.value)}
                                      disabled={readOnly}
                                      class="form-select form-input-small"
                                    >
                                      <option value="">{$_("SelectAnOption")}</option>
                                      {#each itemProperty.enum as option (option)}
                                        <option value={option}>{option}</option>
                                      {/each}
                                    </select>
                                  {:else if itemProperty.maxLength && itemProperty.maxLength > 100}
                                    <textarea
                                      id={`${propName}-${index}-${itemPropName}`}
                                      rows="3"
                                      value={String(itemAt(propName, index)[itemPropName] ?? '')}
                                      oninput={(e) => setItemField(propName, index, itemPropName, e.currentTarget.value)}
                                      disabled={readOnly}
                                      class="form-textarea form-input-small"
                                    ></textarea>
                                  {:else}
                                    <input
                                      id={`${propName}-${index}-${itemPropName}`}
                                      type="text"
                                      value={itemAt(propName, index)[itemPropName] ?? ''}
                                      oninput={(e) => setItemField(propName, index, itemPropName, e.currentTarget.value)}
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
                                    value={itemAt(propName, index)[itemPropName] ?? ''}
                                    oninput={(e) => setItemField(propName, index, itemPropName, e.currentTarget.valueAsNumber)}
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
                                      checked={itemAt(propName, index)[itemPropName] === true}
                                      onchange={(e) => setItemField(propName, index, itemPropName, e.currentTarget.checked)}
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
                            value={item ?? ''}
                            oninput={(e) => setListItem(propName, index, e.currentTarget.value)}
                            disabled={readOnly}
                            class="form-input"
                          />
                        {:else if property.items?.type === "number" || property.items?.type === "integer" || (typeof item === "number")}
                          <input
                            type="number"
                            value={item ?? ''}
                            oninput={(e) => setListItem(propName, index, e.currentTarget.valueAsNumber)}
                            disabled={readOnly}
                            class="form-input"
                          />
                        {:else if property.items?.type === "boolean" || (typeof item === "boolean")}
                          <div class="checkbox-container">
                            <input
                              type="checkbox"
                              checked={item === true}
                              onchange={(e) => setListItem(propName, index, e.currentTarget.checked)}
                              disabled={readOnly}
                              class="form-checkbox"
                            />
                          </div>
                        {:else if isJsonObject(item)}
                          <div class="object-fields">
                            {#each Object.keys(item) as itemKey (itemKey)}
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
                                      checked={item[itemKey] === true}
                                      onchange={(e) => setItemField(propName, index, itemKey, e.currentTarget.checked)}
                                      disabled={readOnly}
                                      class="form-checkbox"
                                    />
                                  </div>
                                {:else if typeof item[itemKey] === "number"}
                                  <input
                                    id={`${propName}-${index}-${itemKey}`}
                                    type="number"
                                    value={item[itemKey] ?? ''}
                                    oninput={(e) => setItemField(propName, index, itemKey, e.currentTarget.valueAsNumber)}
                                    disabled={readOnly}
                                    class="form-input form-input-small"
                                  />
                                {:else}
                                  <input
                                    id={`${propName}-${index}-${itemKey}`}
                                    type="text"
                                    value={item[itemKey] ?? ''}
                                    oninput={(e) => setItemField(propName, index, itemKey, e.currentTarget.value)}
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
                {#each Object.keys(property.properties) as nestedPropName (nestedPropName)}
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
                        value={objectAt(propName)[nestedPropName] ?? ''}
                        oninput={(e) => setObjectField(propName, nestedPropName, e.currentTarget.value)}
                        disabled={readOnly}
                        class="form-input form-input-small"
                      />
                    {:else if nestedProperty.type === "number" || nestedProperty.type === "integer"}
                      <input
                        id={`${propName}-${nestedPropName}`}
                        type="number"
                        value={objectAt(propName)[nestedPropName] ?? ''}
                        oninput={(e) => setObjectField(propName, nestedPropName, e.currentTarget.valueAsNumber)}
                        disabled={readOnly}
                        class="form-input form-input-small"
                      />
                    {:else if nestedProperty.type === "boolean"}
                      <div class="checkbox-container">
                        <input
                          id={`${propName}-${nestedPropName}`}
                          type="checkbox"
                          checked={objectAt(propName)[nestedPropName] === true}
                          onchange={(e) => setObjectField(propName, nestedPropName, e.currentTarget.checked)}
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
        {/if}
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
    color: var(--color-text);
    display: flex;
    align-items: center;
    gap: 4px;
    margin-bottom: 4px;
  }

  .required-indicator {
    color: var(--color-danger);
    font-size: 16px;
    font-weight: 700;
  }

  .field-description {
    font-size: 12px;
    color: var(--color-text-muted);
    margin: 0 0 8px 0;
    line-height: 1.4;
  }

  .form-input,
  .form-select,
  .form-textarea {
    width: 100%;
    padding: 12px 16px;
    border: 2px solid var(--color-border);
    border-radius: 8px;
    font-size: 14px;
    transition: all 0.2s ease;
    background: var(--color-surface-2);
    color: var(--color-text);
    box-sizing: border-box;
  }

  .form-input:focus,
  .form-select:focus,
  .form-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .form-input:hover,
  .form-select:hover,
  .form-textarea:hover {
    border-color: var(--color-border-strong);
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
    border: 2px solid var(--color-border-strong);
    border-radius: 4px;
    cursor: pointer;
    transition: all 0.2s ease;
  }

  .form-checkbox:checked {
    background-color: var(--color-primary);
    border-color: var(--color-primary);
  }

  .checkbox-label {
    font-size: 14px;
    color: var(--color-text-muted);
    font-weight: 500;
  }

  .array-container {
    border: 2px solid var(--color-surface-3);
    border-radius: 12px;
    padding: 20px;
    background: var(--color-surface-2);
  }

  .array-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 16px;
    padding-bottom: 12px;
    border-bottom: 1px solid var(--color-border);
  }

  .array-title {
    font-size: 16px;
    font-weight: 600;
    color: var(--color-text);
    margin: 0;
  }

  .array-items {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .array-item {
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: 8px;
    padding: 16px;
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 16px;
    transition: all 0.2s ease;
  }

  .array-item:hover {
    border-color: var(--color-border-strong);
    box-shadow: 0 2px 4px rgba(0, 0, 0, 0.05);
  }

  .array-item-content {
    flex: 1;
  }

  .array-item-actions {
    flex-shrink: 0;
  }

  .object-container {
    border: 2px solid var(--color-surface-3);
    border-radius: 12px;
    overflow: hidden;
    background: var(--color-surface-2);
  }

  .object-header {
    background: linear-gradient(135deg, var(--color-surface), var(--color-surface-3));
    padding: 16px 20px;
    border-bottom: 1px solid var(--color-border);
  }

  .object-title {
    font-size: 16px;
    font-weight: 600;
    color: var(--color-text);
    margin: 0;
  }

  .object-content {
    padding: 20px;
    background: var(--color-surface-2);
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
    color: var(--color-text-muted);
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
    background: linear-gradient(135deg, var(--color-primary), var(--color-primary-hover));
    color: var(--color-surface-2);
  }

  .btn-primary:hover {
    background: linear-gradient(135deg, var(--color-primary-hover), var(--color-primary-hover));
  }

  .btn-danger {
    background: linear-gradient(135deg, var(--color-danger), var(--color-danger));
    color: var(--color-surface-2);
  }

  .btn-danger:hover {
    background: linear-gradient(135deg, var(--color-danger), var(--color-danger-hover));
  }

  .btn-icon {
    width: 16px;
    height: 16px;
    stroke-width: 2;
  }

  .empty-state {
    text-align: center;
    padding: 48px 24px;
    background: var(--color-surface);
    border-radius: 12px;
    border: 2px dashed var(--color-border-strong);
  }

  .empty-message {
    font-size: 16px;
    color: var(--color-text-muted);
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
