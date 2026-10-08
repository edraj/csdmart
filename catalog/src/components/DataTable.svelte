<script lang="ts">
  import type { Snippet } from "svelte";
  import { _, locale } from "@/i18n";
  import { formatNumber } from "@/lib/helpers";
  import { ELLIPSIS, pageRange, pageWindow } from "@/lib/pagination";
  import { ChevronLeftOutline, ChevronRightOutline } from "flowbite-svelte-icons";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  // The one list table: sticky sentence-case header, keyboard-focusable rows
  // (a real link or button in the first cell, the whole row clickable with the
  // mouse), tabular numerals, selection with bulk actions, and a pager with
  // named prev/next and aria-current. A refresh overlays the rows instead of
  // blanking them.

  interface IndexAttribute {
    key: string;
    name: string | Record<string, string>;
    sortable?: boolean;
  }

  type SortDirection = "asc" | "desc";

  interface CellSnippetContext {
    item: any;
    attr: IndexAttribute;
    index: number;
  }

  interface ActionsSnippetContext {
    item: any;
    index: number;
  }

  interface BulkActionsSnippetContext {
    selectedCount: number;
  }

  interface StateSnippetContext {
    items: any[];
  }

  interface Props {
    items: any[];
    indexAttributes?: IndexAttribute[];
    selectable?: boolean;
    selectedItems?: Set<string>;
    onSelectAll?: (checked: boolean) => void;
    onSelectItem?: (id: string) => void;
    onRowClick?: (item: any, event: MouseEvent | KeyboardEvent) => void;
    /** When given, the row's primary control is a real link to this URL (withBase applied by the caller). */
    rowHref?: (item: any) => string | undefined;
    /** Accessible name of the row's link/button; defaults to the first attribute's value or the id. */
    rowLabel?: (item: any) => string;
    /**
     * Identity of a row for keyed rendering. Defaults to shortname/id, which is
     * unique inside one folder but not across spaces — a listing that mixes
     * spaces (My Entries) must supply a composite key or Svelte throws
     * each_key_duplicate and the page never leaves its loading state.
     */
    rowKey?: (item: any) => string | number;
    loading?: boolean;
    emptyMessage?: string;
    currentPage?: number;
    totalPages?: number;
    totalItems?: number;
    itemsPerPage?: number;
    onPageChange?: (page: number) => void;
    onItemsPerPageChange?: (count: number) => void;
    itemsPerPageOptions?: number[];
    /** Kept for existing callers; direction now follows <html dir>. */
    rtl?: boolean;
    name?: string;
    sortKey?: string | null;
    sortDirection?: SortDirection;
    onSortChange?: (key: string, direction: SortDirection) => void;
    cell: Snippet<[CellSnippetContext]>;
    actions: Snippet<[ActionsSnippetContext]>;
    bulkActions?: Snippet<[BulkActionsSnippetContext]>;
    loadingState?: Snippet<[StateSnippetContext]>;
    emptyState?: Snippet<[StateSnippetContext]>;
  }

  let {
    items = [],
    indexAttributes = [],
    selectable = false,
    selectedItems = new Set(),
    onSelectAll,
    onSelectItem,
    onRowClick,
    rowHref,
    rowLabel,
    rowKey,
    loading = false,
    emptyMessage,
    currentPage = 1,
    totalPages = 1,
    totalItems = 0,
    itemsPerPage = 10,
    onPageChange,
    onItemsPerPageChange,
    itemsPerPageOptions = [10, 25, 50, 100],
    name,
    sortKey = null,
    sortDirection = "asc",
    onSortChange,
    cell,
    actions,
    bulkActions,
    loadingState,
    emptyState,
  }: Props = $props();

  const uid = $props.id();

  let internalSortKey = $state<string | null>(null);
  let internalSortDirection = $state<SortDirection>("asc");

  $effect(() => {
    internalSortKey = sortKey;
    internalSortDirection = sortDirection;
  });

  const defaultIndexAttributes = $derived<IndexAttribute[]>([
    { key: "shortname", name: $_("data_table.columns.shortname") },
    { key: "is_active", name: $_("data_table.columns.status") },
    { key: "created_at", name: $_("data_table.columns.created_at") },
    { key: "updated_at", name: $_("data_table.columns.updated_at") },
  ]);

  const effectiveIndexAttributes = $derived(
    indexAttributes && indexAttributes.length > 0 && indexAttributes.some((attr) => attr && Object.keys(attr).length > 0)
      ? indexAttributes
      : defaultIndexAttributes,
  );

  const allSelected = $derived(selectedItems.size > 0 && selectedItems.size === items.length);
  const someSelected = $derived(selectedItems.size > 0 && selectedItems.size < items.length);
  const showPagination = $derived(totalPages > 1);
  const interactiveRows = $derived(!!onRowClick || !!rowHref);

  const actionsLabel = $derived(name ? (name in { en: 1, ar: 1, ku: 1 } ? $_(name) || "" : name) : $_("actions.name") || "");

  function getAttributeName(attr: IndexAttribute): string {
    if (typeof attr.name === "string") {
      if (attr.name in { en: 1, ar: 1, ku: 1 }) return $_(attr.name + ".name") || "";
      return attr.name;
    }
    if (typeof attr.name === "object" && attr.name !== null) {
      return attr.name[$locale ?? ""] || attr.name.en || attr.name.ar || attr.name.ku || "";
    }
    return "";
  }

  function getItemId(item: any): string {
    return item.shortname || item.id || String(items.indexOf(item));
  }

  function labelOf(item: any): string {
    if (rowLabel) return rowLabel(item);
    const first = effectiveIndexAttributes[0];
    const value = first ? getNestedValue(item, first.key) : null;
    return value == null || typeof value === "object" ? getItemId(item) : String(value);
  }

  function handleSelectAll(e: Event) {
    onSelectAll?.((e.target as HTMLInputElement).checked);
  }

  function handleRowClick(item: any, event: MouseEvent) {
    // Clicks on the row's own controls (checkbox, actions, the primary link)
    // handle themselves; a click on the rest of the row opens the item.
    const target = event.target as HTMLElement;
    if (target.closest("a, button, input, select, textarea, [data-row-stop]")) return;
    onRowClick?.(item, event);
  }

  function goToPage(page: number) {
    if (page >= 1 && page <= totalPages && page !== currentPage) onPageChange?.(page);
  }

  function handleItemsPerPageChange(e: Event) {
    onItemsPerPageChange?.(parseInt((e.target as HTMLSelectElement).value, 10));
  }

  function handleSortClick(attr: IndexAttribute) {
    if (!attr.sortable) return;
    const nextDir: SortDirection = internalSortKey === attr.key && internalSortDirection === "asc" ? "desc" : "asc";
    internalSortKey = attr.key;
    internalSortDirection = nextDir;
    onSortChange?.(attr.key, nextDir);
  }

  function getNestedValue(obj: any, key: string): any {
    if (obj == null) return null;
    if (key in obj) return obj[key];
    if (obj.attributes && key in obj.attributes) return obj.attributes[key];
    let cur: any = obj;
    for (const part of key.split(".")) {
      if (cur == null) return null;
      cur = cur[part];
    }
    return cur;
  }

  function compareValues(a: any, b: any): number {
    if (a == null && b == null) return 0;
    if (a == null) return -1;
    if (b == null) return 1;
    if (typeof a === "number" && typeof b === "number") return a - b;
    const sa = String(a);
    const sb = String(b);
    const na = Number(sa);
    const nb = Number(sb);
    if (!Number.isNaN(na) && !Number.isNaN(nb)) return na - nb;
    return sa.localeCompare(sb, undefined, { sensitivity: "base" });
  }

  // Client-side sort only when the page does not sort on the server.
  const displayItems = $derived.by(() => {
    if (!internalSortKey || onSortChange) return items;
    const key = internalSortKey;
    const dir = internalSortDirection === "desc" ? -1 : 1;
    return [...items].sort((a, b) => compareValues(getNestedValue(a, key), getNestedValue(b, key)) * dir);
  });

  const range = $derived(pageRange(currentPage, itemsPerPage, totalItems));
  const pages = $derived(pageWindow(currentPage, totalPages));
  const num = (n: number) => formatNumber(n, $locale || "en");
</script>

<div class="w-full">
  {#if selectable && selectedItems.size > 0 && bulkActions}
    <div class="flex flex-wrap items-center justify-between gap-3 mb-3 px-4 py-3 rounded-card border border-primary/40 bg-primary-soft" role="region" aria-label={$_("data_table.selection")}>
      <span class="text-sm font-semibold text-text tabular-nums">
        {num(selectedItems.size)}
        {$_("admin_content.bulk_actions.items_selected")}
      </span>
      <div class="flex flex-wrap items-center gap-2">
        {@render bulkActions({ selectedCount: selectedItems.size })}
      </div>
    </div>
  {/if}

  <div class="rounded-card border border-border bg-surface-2 shadow-card overflow-hidden">
    {#if loading && items.length === 0}
      {#if loadingState}
        {@render loadingState({ items })}
      {:else}
        <div class="p-5">
          <LoadingState variant="skeleton" rows={5} />
        </div>
      {/if}
    {:else if items.length === 0}
      {#if emptyState}
        {@render emptyState({ items })}
      {:else}
        <EmptyState
          class="border-0 rounded-none"
          title={$_("admin_content.empty.title")}
          hint={emptyMessage || $_("admin_content.empty.description")}
        />
      {/if}
    {:else}
      <LoadingState variant="overlay" {loading}>
        <div class="overflow-auto max-h-[70vh]">
          <table class="w-full text-sm text-start border-collapse tabular-nums">
            <thead class="sticky top-0 z-10 bg-surface-3 text-xs text-text-muted">
              <tr>
                {#if selectable}
                  <th scope="col" class="px-4 py-3 w-12">
                    <input
                      type="checkbox"
                      checked={allSelected}
                      indeterminate={someSelected}
                      onchange={handleSelectAll}
                      class="w-4 h-4 accent-primary rounded cursor-pointer"
                      aria-label={$_("admin_content.bulk_actions.select_all")}
                    />
                  </th>
                {/if}
                {#each effectiveIndexAttributes as attr (attr.key)}
                  <th
                    scope="col"
                    class="px-4 py-3 text-start font-semibold whitespace-nowrap"
                    aria-sort={attr.sortable && internalSortKey === attr.key
                      ? internalSortDirection === "asc"
                        ? "ascending"
                        : "descending"
                      : attr.sortable
                        ? "none"
                        : undefined}
                  >
                    {#if attr.sortable}
                      <button
                        type="button"
                        class="inline-flex items-center gap-1.5 bg-transparent border-0 p-0 font-semibold text-inherit cursor-pointer hover:text-text rounded-control
 {internalSortKey === attr.key ? 'text-primary' : ''}"
                        onclick={() => handleSortClick(attr)}
                      >
                        <span>{getAttributeName(attr)}</span>
                        <span class="inline-flex w-3.5 h-3.5 items-center justify-center" aria-hidden="true">
                          {#if internalSortKey === attr.key}
                            {#if internalSortDirection === "asc"}
                              <svg viewBox="0 0 12 12" width="10" height="10"><path d="M6 3l4 5H2z" fill="currentColor" /></svg>
                            {:else}
                              <svg viewBox="0 0 12 12" width="10" height="10"><path d="M6 9l4-5H2z" fill="currentColor" /></svg>
                            {/if}
                          {:else}
                            <svg viewBox="0 0 12 12" width="10" height="10" opacity="0.35"><path d="M6 3l3 4H3zM6 9l3-4H3z" fill="currentColor" /></svg>
                          {/if}
                        </span>
                      </button>
                    {:else}
                      {getAttributeName(attr)}
                    {/if}
                  </th>
                {/each}
                <th scope="col" class="px-4 py-3 text-end font-semibold whitespace-nowrap">{actionsLabel}</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-border">
              {#each displayItems as item, index (rowKey ? rowKey(item) : (item.shortname ?? item.id ?? index))}
                {@const itemId = getItemId(item)}
                {@const href = rowHref?.(item)}
                <tr
                  class="group transition-colors {interactiveRows ? 'cursor-pointer hover:bg-surface-3' : ''}
 {selectable && selectedItems.has(itemId) ? 'bg-primary-soft/40' : ''}"
                  onclick={interactiveRows ? (e) => handleRowClick(item, e) : undefined}
                >
                  {#if selectable}
                    <td class="px-4 py-2">
                      <input
                        type="checkbox"
                        checked={selectedItems.has(itemId)}
                        onchange={() => onSelectItem?.(itemId)}
                        class="w-4 h-4 accent-primary rounded cursor-pointer"
                        aria-label={$_("admin_content.bulk_actions.select_item", { values: { name: itemId } })}
                      />
                    </td>
                  {/if}
                  {#each effectiveIndexAttributes as attr, col (attr.key)}
                    <td class="px-4 py-2 text-text {col === 0 && interactiveRows ? 'relative' : ''}">
                      {#if col === 0 && interactiveRows}
                        <!-- The row's real control for keyboard and screen-reader users. -->
                        {#if href}
                          <a {href} class="row-control" aria-label={labelOf(item)} onclick={(e) => onRowClick?.(item, e)}>
                            <span class="sr-only">{labelOf(item)}</span>
                          </a>
                        {:else}
                          <button type="button" class="row-control" aria-label={labelOf(item)} onclick={(e) => onRowClick?.(item, e)}>
                            <span class="sr-only">{labelOf(item)}</span>
                          </button>
                        {/if}
                      {/if}
                      {@render cell({ item, attr, index })}
                    </td>
                  {/each}
                  <td class="px-4 py-2" data-row-stop>
                    <div class="flex items-center justify-end gap-2">
                      {@render actions({ item, index })}
                    </div>
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      </LoadingState>

      <div class="flex flex-wrap items-center justify-between gap-3 px-4 py-3 border-t border-border bg-surface text-sm text-text-muted">
        <div class="flex items-center gap-2">
          <label for="{uid}-per-page">{$_("admin_content.pagination.items_per_page")}</label>
          <select
            id="{uid}-per-page"
            value={itemsPerPage}
            onchange={handleItemsPerPageChange}
            class="h-8 ps-2 pe-7 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
          >
            {#each itemsPerPageOptions as option (option)}
              <option value={option}>{num(option)}</option>
            {/each}
          </select>
        </div>

        {#if showPagination}
          <span class="hidden sm:block tabular-nums">
            {$_("admin_content.pagination.showing", {
              values: { start: num(range.start), end: num(range.end), total: num(totalItems) },
            })}
          </span>

          <nav class="flex items-center gap-1" aria-label={$_("data_table.pagination")}>
            <button
              type="button"
              class="pager-btn"
              onclick={() => goToPage(currentPage - 1)}
              disabled={currentPage <= 1}
              aria-label={$_("admin_content.pagination.previous")}
            >
              <ChevronLeftOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
            </button>

            <span class="sm:hidden px-2 tabular-nums" aria-current="page">
              {num(currentPage)} / {num(totalPages)}
            </span>

            <ul class="hidden sm:flex items-center gap-1 list-none p-0 m-0">
              {#each pages as page, i (typeof page === "number" ? page : `e${i}`)}
                <li>
                  {#if page === ELLIPSIS}
                    <span class="px-1 text-text-faint" aria-hidden="true">{ELLIPSIS}</span>
                  {:else}
                    <button
                      type="button"
                      class="pager-btn {page === currentPage ? 'pager-btn-active' : ''}"
                      aria-current={page === currentPage ? "page" : undefined}
                      aria-label={$_("data_table.go_to_page", { values: { page: num(page) } })}
                      onclick={() => goToPage(page)}
                    >
                      {num(page)}
                    </button>
                  {/if}
                </li>
              {/each}
            </ul>

            <button
              type="button"
              class="pager-btn"
              onclick={() => goToPage(currentPage + 1)}
              disabled={currentPage >= totalPages}
              aria-label={$_("admin_content.pagination.next")}
            >
              <ChevronRightOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
            </button>
          </nav>
        {:else}
          <span class="tabular-nums">
            {$_("admin_content.pagination.total_items", { values: { total: num(totalItems || items.length) } })}
          </span>
        {/if}
      </div>
    {/if}
  </div>
</div>

<style>
  /* The focusable control that stands for the whole first cell. */
  .row-control {
    position: absolute;
    inset: 0;
    display: block;
    background: transparent;
    border: 0;
    padding: 0;
    cursor: pointer;
    border-radius: var(--radius-control);
  }

  .row-control:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: -2px;
  }

  .pager-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 2rem;
    height: 2rem;
    padding: 0 0.5rem;
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text-muted);
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-control);
    cursor: pointer;
    transition: background var(--duration-fast) var(--ease-out), color var(--duration-fast) var(--ease-out);
  }

  .pager-btn:hover:not(:disabled) {
    background: var(--color-surface-3);
    color: var(--color-text);
  }

  .pager-btn:disabled {
    opacity: 0.4;
    cursor: not-allowed;
  }

  .pager-btn-active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: var(--color-text-on-primary);
  }

  .pager-btn-active:hover:not(:disabled) {
    background: var(--color-primary-hover);
    color: var(--color-text-on-primary);
  }
</style>
