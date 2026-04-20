<script lang="ts">
  import type { JsonTableTheme, JsonSchema, JsonValue, JsonPath, OnUpdate } from "./types";
  import EditableValue from "./EditableValue.svelte";
  import TableRow from "./TableRow.svelte";
  import JsonTable from "./JsonTable.svelte";

  interface Props {
    data: JsonValue;
    schema?: JsonSchema;
    label?: string;
    depth?: number;
    path?: JsonPath;
    theme: JsonTableTheme;
    onUpdate: OnUpdate;
  }

  let {
    data,
    schema = undefined,
    label = undefined,
    depth = 0,
    path = [],
    theme: t,
    onUpdate,
  }: Props = $props();

  interface Entry {
    key: string;
    value: JsonValue;
  }

  const isArray: boolean = $derived(Array.isArray(data));
  const isObject: boolean = $derived(data !== null && typeof data === "object");

  const entries: Entry[] = $derived(
    isArray
      ? (data as JsonValue[]).map((v, i) => ({ key: String(i), value: v }))
      : isObject
        ? Object.entries(data as Record<string, JsonValue>).map(([k, v]) => ({ key: k, value: v }))
        : []
  );

  const headerBg: string = $derived(t.headers[Math.min(depth, t.headers.length - 1)]);

  const headerLabel: string | undefined = $derived(
    label !== undefined ? getSchemaLabel(schema, label) : undefined
  );

  function isPrimitive(v: JsonValue): boolean {
    return v === null || typeof v !== "object";
  }

  function getSchemaLabel(s: JsonSchema | undefined, fallback: string): string {
    const title = s?.title;
    return title?.trim() ? title : fallback;
  }

  function getDisplayName(key: string): string {
    if (isArray) return key;
    const title = schema?.properties?.[key]?.title;
    return title?.trim() ? title : key;
  }

  function getChildSchema(key: string): JsonSchema | undefined {
    if (!schema) return undefined;
    if (isArray) return schema.items;
    return schema.properties?.[key];
  }
</script>

{#if isObject}
  <div
    class="jt-wrap"
    class:jt-root={depth === 0}
    class:jt-nested={depth > 0}
    style:--jt-border={t.outerBorder}
    style:--jt-bg={t.bg}
  >
    {#if headerLabel !== undefined}
      <div class="jt-header" style:background={headerBg} style:color={t.headerText}>
        {headerLabel}{#if isArray}{" [ ]"}{/if}
      </div>
    {/if}

    <table class="jt-table">
      <tbody>
        {#each entries as entry, i (entry.key)}
          {@const nested = !isPrimitive(entry.value)}
          {@const odd = i % 2 === 1}
          {@const childPath: JsonPath = [...path, isArray ? Number(entry.key) : entry.key]}

          <TableRow
            displayName={getDisplayName(entry.key)}
            isArray={isArray}
            isOdd={odd}
            isNested={nested}
            theme={t}
          >
            {#if nested}
              <JsonTable
                data={entry.value}
                schema={getChildSchema(entry.key)}
                depth={depth + 1}
                path={childPath}
                theme={t}
                {onUpdate}
              />
            {:else}
              <EditableValue
                value={entry.value as import("./types").JsonPrimitive}
                theme={t}
                onUpdate={(v) => onUpdate(childPath, v)}
              />
            {/if}
          </TableRow>
        {/each}
      </tbody>
    </table>
  </div>
{:else}
  <EditableValue
    value={data as import("./types").JsonPrimitive}
    theme={t}
    onUpdate={(v) => onUpdate(path, v)}
  />
{/if}

<style>
  .jt-wrap {
    border-radius: 4px;
    overflow: hidden;
    border: 1px solid var(--jt-border);
    background: var(--jt-bg);
    font-size: 13px;
    vertical-align: top;
    font-family: 'IBM Plex Sans', system-ui, -apple-system, sans-serif;
  }
  .jt-root {
    display: block;
    border-radius: 6px;
  }
  .jt-nested {
    display: block;
  }
  .jt-header {
    padding: 5px 14px;
    font-weight: 600;
    font-size: 0.9em;
    letter-spacing: 0.02em;
    text-align: center;
  }
  .jt-table {
    border-collapse: collapse;
    width: 100%;
  }
</style>
