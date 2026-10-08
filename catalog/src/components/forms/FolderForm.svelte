<script lang="ts">
  import { Dmart, QueryType } from "@edraj/tsdmart";
  import { _ } from "svelte-i18n";
  import { applyFolderContentDefaults, type FolderContent } from "@/lib/folder_defaults";

  type SelectChangeEvent = Event & { currentTarget: EventTarget & HTMLSelectElement };

  let {
    content = $bindable(applyFolderContentDefaults({})),
    space_name = $bindable(""),
    fullWidth = false,
  }: {
    /** The folder's listing settings (`payload.body`); defaults are applied on mount. */
    content: FolderContent;
    space_name: string;
    fullWidth?: boolean;
  } = $props();

  content = applyFolderContentDefaults(content);

  function handleResourceTypeChange(e: SelectChangeEvent) {
    const target = e.currentTarget;
    if (target.value) {
      content.content_resource_types = [target.value];
    } else {
      content.content_resource_types = [];
    }
  }

  function addSchemaShortname(e: SelectChangeEvent) {
    const select = e.currentTarget;
    if (
      select.value &&
      !content.content_schema_shortnames.includes(select.value)
    ) {
      content.content_schema_shortnames = [
        ...content.content_schema_shortnames,
        select.value,
      ];
      select.value = "";
    }
  }

  function removeSchemaShortname(schema: string) {
    content.content_schema_shortnames =
      content.content_schema_shortnames.filter((s) => s !== schema);
  }

  function addWorkflowShortname(e: SelectChangeEvent) {
    const select = e.currentTarget;
    if (
      select.value &&
      !content.workflow_shortnames.includes(select.value)
    ) {
      content.workflow_shortnames = [
        ...content.workflow_shortnames,
        select.value,
      ];
      select.value = "";
    }
  }

  function removeWorkflowShortname(workflow: string) {
    content.workflow_shortnames = content.workflow_shortnames.filter(
      (w) => w !== workflow
    );
  }
</script>

<div class="editor-card" class:editor-card-full={fullWidth}>
  <h2 class="editor-title">{$_("editor.title")}</h2>

  <div class="editor-content">
    <!-- Sort Settings -->
    <div class="grid-2">
      <div class="field-group">
        <label for="sort_by" class="field-label">{$_("fields.sort_by")}</label>
        <input
          id="sort_by"
          class="input-field"
          placeholder={$_("placeholders.sort_by")}
          bind:value={content.sort_by}
        />
      </div>

      <div class="field-group">
        <label for="sort_type" class="field-label"
          >{$_("fields.sort_order")}</label
        >
        <select
          id="sort_type"
          class="select-field"
          bind:value={content.sort_type}
        >
          <option value="">{$_("options.select_sort_order")}</option>
          <option value="ascending">{$_("options.ascending")}</option>
          <option value="descending">{$_("options.descending")}</option>
        </select>
      </div>
    </div>

    <!-- Content Resource Types -->
    <div class="section">
      <h3 class="section-title">
        {$_("sections.content_resource_types.title")}
      </h3>
      <div class="field-group">
        <select
          id="resource-type-select"
          class="select-field"
          onchange={handleResourceTypeChange}
        >
          <option value="">{$_("options.select_type")}</option>
          <option
            value="ticket"
            selected={content.content_resource_types.includes("ticket")}
          >
            {$_("resource_types.ticket")}
          </option>
          <option
            value="content"
            selected={content.content_resource_types.includes("content")}
          >
            {$_("resource_types.content")}
          </option>
        </select>
      </div>
    </div>

    <!-- Schema Shortnames -->
    <div class="section">
      <h3 class="section-title">{$_("sections.schema_shortnames.title")}</h3>
      <div class="field-group">
        <select class="select-field" onchange={addSchemaShortname}>
          <option value="">{$_("options.select_schema_to_add")}</option>
          {#await Dmart.query( { space_name: space_name, type: QueryType.search, subpath: "/schema", search: "", retrieve_json_payload: true, limit: 99 } ) then schemas}
            {#each schemas!.records.map((e) => e.shortname) as schema (schema)}
              <option value={schema}>{schema}</option>
            {/each}
          {:catch}
            <option disabled>{$_("errors.loading_schemas")}</option>
          {/await}
        </select>

        {#if content.content_schema_shortnames.length > 0}
          <div class="tags-container">
            {#each content.content_schema_shortnames as schema (schema)}
              <span class="tag">
                {schema}
                <button
                  aria-label={$_("labels.remove_schema_named", { values: { name: schema } })}
                  type="button"
                  class="tag-remove"
                  onclick={() => removeSchemaShortname(schema)}
                >
                  ×
                </button>
              </span>
            {/each}
          </div>
        {/if}
      </div>
    </div>

    <!-- Workflow Shortnames -->
    <div class="section">
      <h3 class="section-title">{$_("sections.workflow_shortnames.title")}</h3>
      <div class="field-group">
        <select class="select-field" onchange={addWorkflowShortname}>
          <option value="">{$_("options.select_workflow_to_add")}</option>
          {#await Dmart.query( { space_name: "management", type: QueryType.search, subpath: "/workflow", search: "", retrieve_json_payload: true, limit: 99 } ) then workflows}
            {#each workflows!.records.map((e) => e.shortname) as workflow (workflow)}
              <option value={workflow}>{workflow}</option>
            {/each}
          {:catch}
            <option disabled>{$_("errors.loading_workflows")}</option>
          {/await}
        </select>

        {#if content.workflow_shortnames.length > 0}
          <div class="tags-container">
            {#each content.workflow_shortnames as workflow (workflow)}
              <span class="tag">
                {workflow}
                <button
                  aria-label={$_("labels.remove_workflow_named", { values: { name: workflow } })}
                  type="button"
                  class="tag-remove"
                  onclick={() => removeWorkflowShortname(workflow)}
                >
                  ×
                </button>
              </span>
            {/each}
          </div>
        {/if}
      </div>
    </div>
  </div>
</div>

<style>
  .editor-card {
    background: var(--color-surface);
    border-radius: 12px;
    box-shadow:
      0 4px 6px -1px rgba(0, 0, 0, 0.1),
      0 2px 4px -1px rgba(0, 0, 0, 0.06);
    max-width: 90rem;
    margin: 0.5rem auto;
    padding: 1.5rem;
    border: 1px solid var(--color-border);
  }

  .editor-card-full {
    max-width: 100%;
    margin: 0;
    padding: 0;
    border: none;
    box-shadow: none;
  }

  .editor-title {
    font-size: 1.25rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 1.5rem;
    margin-top: 0;
  }

  .editor-content {
    display: flex;
    flex-direction: column;
    gap: 2rem;
  }

  .section {
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    padding: 1.5rem;
    background-color: var(--color-surface-2);
  }

  .section-title {
    font-size: 1rem;
    font-weight: 600;
    color: var(--color-text);
    margin-bottom: 0.5rem;
    margin-top: 0;
  }

  .grid-2 {
    display: grid;
    grid-template-columns: 1fr;
    gap: 1.5rem;
  }

  @media (min-width: 768px) {
    .grid-2 {
      grid-template-columns: repeat(2, 1fr);
    }
  }

  .field-group {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .field-label {
    font-weight: 500;
    font-size: 0.875rem;
    color: var(--color-text);
  }

  .input-field {
    padding: 0.625rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.5rem;
    font-size: 0.875rem;
    transition: all 0.15s ease-in-out;
    background: var(--color-surface);
    width: 100%;
  }

  .input-field:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .select-field {
    padding: 0.625rem 0.75rem;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.5rem;
    font-size: 0.875rem;
    background: var(--color-surface);
    cursor: pointer;
    transition: all 0.15s ease-in-out;
    width: 100%;
  }

  .select-field:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .tags-container {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    margin-top: 0.75rem;
  }

  .tag {
    background-color: var(--color-info-soft);
    color: var(--color-info);
    padding: 0.375rem 0.5rem;
    font-size: 0.75rem;
    border-radius: 0.375rem;
    display: flex;
    align-items: center;
    gap: 0.375rem;
    border: 1px solid var(--color-info-soft);
  }

  .tag-remove {
    background: none;
    border: none;
    color: var(--color-info);
    cursor: pointer;
    font-size: 1rem;
    line-height: 1;
    padding: 0;
    width: 1rem;
    height: 1rem;
    display: flex;
    align-items: center;
    justify-content: center;
    border-radius: 50%;
    transition: background-color 0.15s ease-in-out;
  }

  .tag-remove:hover {
    background-color: var(--color-info);
    color: white;
  }

  option:disabled {
    color: var(--color-text-faint);
    font-style: italic;
  }

  @media (max-width: 640px) {
    .tags-container {
      margin-top: 0.5rem;
    }
  }
</style>
