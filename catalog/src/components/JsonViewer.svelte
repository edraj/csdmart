<script lang="ts">
  import { _ } from "@/i18n";
  /**
   * JsonViewer.svelte
   *
   * Drop-in replacement for PlantUMLViewer.
   * Renders JSON as a human-readable table with optional inline editing,
   * schema-aware titles, and save-to-DMART support.
   *
   * Schema wiring:
   *   - Pass `schema` directly if you already have the JSON Schema object, OR
   *   - Pass `schemaShortname` + `spaceName` and the component auto-fetches it
   *     from DMART at /{spaceName}/schema/{schemaShortname}
   *
   * Read-only (same API as old PlantUMLViewer):
   *   <JsonViewer data={entity.payload.body} title="My Entry" />
   *
   * With auto-loaded schema titles:
   *   <JsonViewer
   *     data={entity.payload.body}
   *     title="My Entry"
   *     schemaShortname={entity.payload.schema_shortname}
   *     spaceName={$params.space_name}
   *   />
   *
   * Editable with save:
   *   <JsonViewer
   *     data={entity.payload.body}
   *     title="My Entry"
   *     editable={true}
   *     schemaShortname={entity.payload.schema_shortname}
   *     spaceName={$params.space_name}
   *     subpath={$params.subpath}
   *     shortname={$params.shortname}
   *     onSaved={(updatedData) => { entity.payload.body = updatedData; }}
   *   />
   */

  import JsonTable from "./json-table/JsonTable.svelte";
  import { themes, type JsonTableTheme, type JsonSchema, type JsonValue, type JsonPath } from "./json-table/types";
  import { updateEntity, getEntityByShortname } from "@/lib/dmart_services/core";
  import { ResourceType, DmartScope } from "@edraj/tsdmart";
  import { successToastMessage, errorToastMessage } from "@/lib/toasts_messages";
  import { log } from "@/lib/logger";

  interface Props {
    /** The JSON data to display */
    data: any;
    /** Header title */
    title?: string;
    /** Diagram type — kept for backward compat, only "json" is rendered as table */
    type?: "json" | "class";
    /** Whether the admin payload toggle is shown (backward compat) */
    isAdmin?: boolean;
    /** JSON Schema object — pass directly if already available */
    schema?: JsonSchema;
    /**
     * DMART schema shortname (e.g. entity.payload.schema_shortname).
     * When provided along with spaceName, the component auto-fetches the
     * schema from /{spaceName}/schema/{schemaShortname} and uses its
     * property titles for display names.
     */
    schemaShortname?: string;
    /** Enable inline editing + save bar */
    editable?: boolean;
    /** Theme name or theme object */
    theme?: "light" | "dark" | JsonTableTheme;
    /** DMART space name — needed for schema fetch and saving */
    spaceName?: string;
    /** DMART subpath — needed for saving */
    subpath?: string;
    /** DMART entity shortname — needed for saving */
    shortname?: string;
    /** DMART resource type — needed for saving */
    resourceType?: ResourceType;
    /** Callback after successful save, receives the updated data */
    onSaved?: (data: any) => void;
  }

  let {
    data = {},
    title = "View",
    isAdmin = false,
    schema = undefined,
    schemaShortname = undefined,
    editable = false,
    theme: themeProp = "light",
    spaceName = undefined,
    subpath = undefined,
    shortname = undefined,
    resourceType = ResourceType.content,
    onSaved = undefined,
  }: Props = $props();

  /* ── State ── */
  // Starts as a copy of `data` and follows it when the prop changes; edits
  // write to it in place (writable $derived).
  let editData: any = $derived(safeClone(data));
  let saving: boolean = $state(false);
  let saveFlash: boolean = $state(false);
  let showRawPayload: boolean = $state(false);
  let fetchedSchema: JsonSchema | undefined = $state(undefined);
  let loadingSchema: boolean = $state(false);

  /**
   * Deep-clone that tolerates values `structuredClone` rejects — Svelte state
   * proxies, functions inside nested attributes, class instances, etc. Falls
   * back to JSON round-trip, which drops anything non-serializable rather
   * than throwing.
   */
  function safeClone<T>(value: T): T {
    try {
      return structuredClone(value);
    } catch {
      try {
        return JSON.parse(JSON.stringify(value ?? null));
      } catch {
        return value;
      }
    }
  }

  // Auto-fetch schema from DMART when schemaShortname is provided
  $effect(() => {
    if (schemaShortname && spaceName && !schema) {
      fetchSchema(schemaShortname, spaceName);
    }
  });

  /** The resolved schema: explicit prop takes priority, then fetched */
  const resolvedSchema: JsonSchema | undefined = $derived(schema ?? fetchedSchema);

  const t: JsonTableTheme = $derived(
    typeof themeProp === "string" ? themes[themeProp] : themeProp
  );

  const isDirty: boolean = $derived(
    editable && editData != null && JSON.stringify(editData) !== JSON.stringify(data)
  );

  const canSave: boolean = $derived(
    !!(spaceName && subpath && shortname)
  );

  /* ── Schema fetching ── */
  async function fetchSchema(schemaName: string, space: string): Promise<void> {
    loadingSchema = true;
    try {
      const response: any = await getEntityByShortname(
        schemaName,
        space,
        "/schema",
        ResourceType.content,
        DmartScope.managed,
        true,
        false
      );
      if (response?.payload?.body) {
        fetchedSchema = response.payload.body as JsonSchema;
      }
    } catch (error) {
      log.warn(`Could not load schema "${schemaName}" from ${space}/schema:`, error);
    } finally {
      loadingSchema = false;
    }
  }

  /* ── Data helpers ── */
  function setAtPath(obj: any, path: JsonPath, value: JsonValue): any {
    const clone = safeClone(obj);
    let cursor: any = clone;
    for (let i = 0; i < path.length - 1; i++) {
      cursor = cursor[path[i]];
    }
    cursor[path[path.length - 1] as string] = value;
    return clone;
  }

  function handleUpdate(path: JsonPath, value: JsonValue): void {
    if (!editable) return;
    editData = setAtPath(editData, path, value);
  }

  function handleDiscard(): void {
    editData = safeClone(data);
  }

  async function handleSave(): Promise<void> {
    if (!canSave || !spaceName || !subpath || !shortname) {
      // No entity context — emit data via callback only
      onSaved?.(editData);
      saveFlash = true;
      setTimeout(() => { saveFlash = false; }, 2000);
      return;
    }

    saving = true;
    try {
      const success = await updateEntity(
        shortname,
        spaceName,
        subpath,
        resourceType,
        { payload: { content_type: "json", body: editData } }
      );

      if (success) {
        onSaved?.(editData);
        saveFlash = true;
        setTimeout(() => { saveFlash = false; }, 2000);
        successToastMessage("Changes saved successfully");
      } else {
        errorToastMessage("Failed to save changes");
      }
    } catch (error) {
      log.error("Save error:", error);
      errorToastMessage("Failed to save changes");
    } finally {
      saving = false;
    }
  }

  function togglePayload(): void {
    showRawPayload = !showRawPayload;
  }
</script>

<div class="json-viewer" style:--jv-bg={t.bg} style:--jv-border={t.outerBorder}>
  <!-- Toolbar -->
  <div class="jv-toolbar">
    <div class="jv-toolbar-left">
      <span class="jv-badge">JSON</span>
      {#if editable}
        <span class="jv-badge jv-badge-edit">Editable</span>
      {/if}
      {#if loadingSchema}
        <span class="jv-badge jv-badge-loading">Loading schema…</span>
      {/if}
    </div>
    <div class="jv-toolbar-right">
      {#if isAdmin}
        <button class="jv-toolbar-btn" onclick={togglePayload}>
          {showRawPayload ? "Hide Payload" : "Show Payload"}
        </button>
      {/if}
    </div>
  </div>

  <!-- Save flash -->
  {#if saveFlash}
    <div class="jv-flash">
      <span>✓</span>
      <span>{$_("json_viewer.saved")}</span>
    </div>
  {/if}

  <!-- Table -->
  <div class="jv-table-area">
    <JsonTable
      data={editable ? editData : data}
      schema={resolvedSchema}
      label={title}
      theme={t}
      onUpdate={handleUpdate}
    />
  </div>

  <!-- Raw payload (admin) -->
  {#if showRawPayload && isAdmin}
    <div class="jv-code-panel">
      <div class="jv-code-header">JSON Payload</div>
      <pre class="jv-code-block"><code>{JSON.stringify(editable ? editData : data, null, 2)}</code></pre>
    </div>
  {/if}
</div>

<!-- Sticky save bar -->
{#if editable && isDirty}
  <div class="jv-save-bar">
    <div class="jv-save-bar-inner">
      <span class="jv-save-label">
        Unsaved changes
      </span>
      <div class="jv-save-actions">
        <button class="jv-btn-discard" onclick={handleDiscard} disabled={saving}>
          Discard
        </button>
        <button class="jv-btn-save" onclick={handleSave} disabled={saving}>
          {#if saving}
            Saving…
          {:else}
            Save
          {/if}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .json-viewer {
    background: var(--jv-bg, var(--color-surface-2));
    border: 1px solid var(--jv-border, var(--color-border));
    border-radius: 8px;
    overflow: hidden;
    margin: 1rem 0;
  }

  .jv-toolbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px;
    background: var(--color-surface);
    border-bottom: 1px solid var(--color-border);
  }

  .jv-toolbar-left, .jv-toolbar-right {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .jv-badge {
    display: inline-flex;
    align-items: center;
    padding: 4px 10px;
    background: var(--color-info-soft);
    color: var(--color-info);
    border-radius: 9999px;
    font-size: 11px;
    font-weight: 600;
    text-transform: uppercase;
  }

  .jv-badge-edit {
    background: var(--color-warning-soft);
    color: var(--color-warning);
  }

  .jv-badge-loading {
    background: var(--color-primary-soft);
    color: var(--color-primary-hover);
    font-weight: 500;
    text-transform: none;
  }

  .jv-toolbar-btn {
    padding: 6px 12px;
    border: 1px solid var(--color-border-strong);
    border-radius: 6px;
    background: transparent;
    color: var(--color-text-muted);
    font-size: 12px;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s;
  }

  .jv-toolbar-btn:hover {
    background: var(--color-border);
  }

  .jv-flash {
    padding: 8px 16px;
    background: var(--color-success-soft);
    border-bottom: 1px solid var(--color-success);
    font-size: 12px;
    color: var(--color-success);
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .jv-table-area {
    padding: 16px;
    overflow: auto;
  }

  .jv-code-panel {
    border-top: 1px solid var(--color-border);
  }

  .jv-code-header {
    padding: 8px 16px;
    background: var(--color-surface-3);
    border-bottom: 1px solid var(--color-border);
    font-size: 11px;
    font-weight: 600;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }

  .jv-code-block {
    margin: 0;
    padding: 16px;
    background: var(--color-text);
    color: var(--color-border);
    font-family: ui-monospace, "Cascadia Code", "Source Code Pro", Menlo, monospace;
    font-size: 12px;
    line-height: 1.5;
    overflow-x: auto;
    max-height: 300px;
    overflow-y: auto;
  }

  .jv-save-bar {
    position: fixed;
    bottom: 0;
    inset-inline-start: 0;
    inset-inline-end: 0;
    z-index: 50;
    animation: slideUp 0.25s cubic-bezier(0.4, 0, 0.2, 1);
  }

  @keyframes slideUp {
    from { transform: translateY(100%); }
    to { transform: translateY(0); }
  }

  .jv-save-bar-inner {
    max-width: 800px;
    margin: 0 auto;
    padding: 12px 16px;
    background: rgba(255, 255, 255, 0.95);
    backdrop-filter: blur(12px);
    border-top: 1px solid rgba(0, 0, 0, 0.08);
    display: flex;
    align-items: center;
    justify-content: space-between;
    font-family: 'IBM Plex Sans', system-ui, sans-serif;
  }

  .jv-save-label {
    font-size: 13px;
    color: var(--color-text-muted);
  }

  .jv-save-actions {
    display: flex;
    gap: 8px;
  }

  .jv-btn-discard {
    padding: 7px 16px;
    border: 1px solid rgba(0, 0, 0, 0.12);
    border-radius: 6px;
    background: transparent;
    color: var(--color-text);
    cursor: pointer;
    font-size: 13px;
    font-weight: 500;
    transition: background 0.15s;
  }

  .jv-btn-discard:hover:not(:disabled) {
    background: rgba(0, 0, 0, 0.04);
  }

  .jv-btn-save {
    padding: 7px 20px;
    border: none;
    border-radius: 6px;
    background: var(--color-primary);
    color: var(--color-surface-2);
    cursor: pointer;
    font-size: 13px;
    font-weight: 600;
    transition: background 0.15s;
  }

  .jv-btn-save:hover:not(:disabled) {
    background: var(--color-primary);
  }

  .jv-btn-save:disabled, .jv-btn-discard:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }
</style>
