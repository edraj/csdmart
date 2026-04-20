<script lang="ts">
  import type { Snippet } from "svelte";
  import { _, locale } from "@/i18n";
  import { formatNumber } from "@/lib/helpers";
  import SkeletonBlock from "@/components/SkeletonBlock.svelte";

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
    onRowClick?: (item: any, event: MouseEvent) => void;
    loading?: boolean;
    emptyMessage?: string;
    currentPage?: number;
    totalPages?: number;
    totalItems?: number;
    itemsPerPage?: number;
    onPageChange?: (page: number) => void;
    onItemsPerPageChange?: (count: number) => void;
    itemsPerPageOptions?: number[];
    rtl?: boolean;
    name?: string;
    class?: string;
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
    loading = false,
    emptyMessage,
    currentPage = 1,
    totalPages = 1,
    totalItems = 0,
    itemsPerPage = 10,
    onPageChange,
    onItemsPerPageChange,
    itemsPerPageOptions = [10, 25, 50, 100],
    rtl = false,
    name,
    class: className = "",
    sortKey = null,
    sortDirection = "asc",
    onSortChange,
    cell,
    actions,
    bulkActions,
    loadingState,
    emptyState,
  }: Props = $props();

  let internalSortKey = $state<string | null>(null);
  let internalSortDirection = $state<SortDirection>("asc");

  $effect(() => {
    internalSortKey = sortKey;
    internalSortDirection = sortDirection;
  });

  const defaultIndexAttributes: IndexAttribute[] = [
    { key: "shortname", name: "Shortname" },
    { key: "is_active", name: "Status" },
    { key: "created_at", name: "Created At" },
    { key: "updated_at", name: "Updated At" },
  ];

  const effectiveIndexAttributes = $derived(
    indexAttributes &&
      indexAttributes.length > 0 &&
      indexAttributes.some((attr) => attr && Object.keys(attr).length > 0)
      ? indexAttributes
      : defaultIndexAttributes,
  );

  const allSelected = $derived(
    selectedItems.size > 0 && selectedItems.size === items.length,
  );

  const someSelected = $derived(
    selectedItems.size > 0 && selectedItems.size < items.length,
  );

  const showPagination = $derived(totalPages > 1);

  const actionsLabel = $derived(
    name
      ? name in { en: 1, ar: 1, ku: 1 }
        ? ($_(name) || "")
        : name
      : ($_("actions.name") || ""),
  );

  function getAttributeName(attr: IndexAttribute): string {
    if (typeof attr.name === "string") {
      if (attr.name in { en: 1, ar: 1, ku: 1 }) {
        return $_(attr.name + ".name") || "";
      }
      return attr.name;
    }
    if (typeof attr.name === "object" && attr.name !== null) {
      return attr.name.en || attr.name.ar || attr.name.ku || "";
    }
    return "";
  }

  function getItemId(item: any): string {
    return item.shortname || item.id || String(items.indexOf(item));
  }

  function handleSelectAll(e: Event) {
    const checked = (e.target as HTMLInputElement).checked;
    onSelectAll?.(checked);
  }

  function handleSelectItem(id: string) {
    onSelectItem?.(id);
  }

  function handleRowClick(item: any, event: MouseEvent) {
    onRowClick?.(item, event);
  }

  function goToPage(page: number) {
    if (page >= 1 && page <= totalPages) {
      onPageChange?.(page);
    }
  }

  function nextPage() {
    if (currentPage < totalPages) {
      onPageChange?.(currentPage + 1);
    }
  }

  function previousPage() {
    if (currentPage > 1) {
      onPageChange?.(currentPage - 1);
    }
  }

  function handleItemsPerPageChange(e: Event) {
    const value = parseInt((e.target as HTMLSelectElement).value, 10);
    onItemsPerPageChange?.(value);
  }

  function handleSortClick(attr: IndexAttribute) {
    if (!attr.sortable) return;
    const nextDir: SortDirection =
      internalSortKey === attr.key && internalSortDirection === "asc"
        ? "desc"
        : "asc";
    internalSortKey = attr.key;
    internalSortDirection = nextDir;
    onSortChange?.(attr.key, nextDir);
  }

  function handleSortKeydown(e: KeyboardEvent, attr: IndexAttribute) {
    if (!attr.sortable) return;
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      handleSortClick(attr);
    }
  }

  function getNestedValue(obj: any, key: string): any {
    if (obj == null) return null;
    if (key in obj) return obj[key];
    if (obj.attributes && key in obj.attributes) return obj.attributes[key];
    const parts = key.split(".");
    let cur: any = obj;
    for (const part of parts) {
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

  const displayItems = $derived.by(() => {
    if (!internalSortKey || onSortChange) return items;
    const key = internalSortKey;
    const dir = internalSortDirection === "desc" ? -1 : 1;
    return [...items].sort(
      (a, b) => compareValues(getNestedValue(a, key), getNestedValue(b, key)) * dir,
    );
  });

  const paginationStart = $derived(
    totalItems === 0 ? 0 : (currentPage - 1) * itemsPerPage + 1,
  );

  const paginationEnd = $derived(
    Math.min(currentPage * itemsPerPage, totalItems),
  );
</script>

<div class="data-table-container" class:rtl>
  {#if selectable && selectedItems.size > 0 && bulkActions}
    <div class="bulk-actions-bar" class:rtl>
      <div class="bulk-actions-content">
        <div class="bulk-actions-info">
          <span class="bulk-actions-count">
            {selectedItems.size}
            {$_("admin_content.bulk_actions.items_selected")}
          </span>
        </div>
        <div class="bulk-actions-buttons">
          {@render bulkActions({ selectedCount: selectedItems.size })}
        </div>
      </div>
    </div>
  {/if}

  <div class="data-table-card">
    {#if loading}
      {#if loadingState}
        {@render loadingState({ items })}
      {:else}
        <div class="skeleton-rows" aria-busy="true" aria-label={$_("loading") || "Loading..."}>
          {#each Array(5) as _skeletonRow}
            <div class="skeleton-row">
              <SkeletonBlock width="28%" height="0.875rem" />
              <SkeletonBlock width="18%" height="0.875rem" />
              <SkeletonBlock width="22%" height="0.875rem" />
              <SkeletonBlock width="16%" height="0.875rem" />
              <SkeletonBlock width="10%" height="1.25rem" radius="var(--radius-full)" />
            </div>
          {/each}
        </div>
      {/if}
    {:else if items.length === 0}
      {#if emptyState}
        {@render emptyState({ items })}
      {:else}
        <div class="empty-state">
          <div class="empty-state-icon">
            <svg
              class="w-8 h-8"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M9 13h6m-3-3v6m5 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
              />
            </svg>
          </div>
          <h3 class="empty-state-title">
            {$_("admin_content.empty.title") || "No items found"}
          </h3>
          <p class="empty-state-description">
            {emptyMessage || $_("admin_content.empty.description") || "There are no items to display."}
          </p>
        </div>
      {/if}
    {:else}
      <div class="overflow-x-auto">
        <table class="w-full text-left border-collapse">
          <thead>
            <tr class="border-b border-gray-100">
              {#if selectable}
                <th class="px-4 py-4 w-12">
                  <input
                    type="checkbox"
                    checked={allSelected}
                    indeterminate={someSelected}
                    onchange={handleSelectAll}
                    class="w-4 h-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500 cursor-pointer"
                    aria-label={$_("admin_content.bulk_actions.select_all")}
                  />
                </th>
              {/if}
              {#each effectiveIndexAttributes as attr}
                <th
                  class="px-6 py-4 text-xs font-semibold text-gray-500 uppercase tracking-wider"
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
                      class="th-sort-btn"
                      class:is-active={internalSortKey === attr.key}
                      onclick={() => handleSortClick(attr)}
                      onkeydown={(e) => handleSortKeydown(e, attr)}
                    >
                      <span>{getAttributeName(attr)}</span>
                      <span class="th-sort-indicator" aria-hidden="true">
                        {#if internalSortKey === attr.key}
                          {#if internalSortDirection === "asc"}
                            <svg viewBox="0 0 12 12" width="10" height="10"><path d="M6 3l4 5H2z" fill="currentColor"/></svg>
                          {:else}
                            <svg viewBox="0 0 12 12" width="10" height="10"><path d="M6 9l4-5H2z" fill="currentColor"/></svg>
                          {/if}
                        {:else}
                          <svg viewBox="0 0 12 12" width="10" height="10" opacity="0.35"><path d="M6 3l3 4H3zM6 9l3-4H3z" fill="currentColor"/></svg>
                        {/if}
                      </span>
                    </button>
                  {:else}
                    {getAttributeName(attr)}
                  {/if}
                </th>
              {/each}
              <th class="px-6 py-4 text-right text-xs font-semibold text-gray-500 uppercase tracking-wider">
                {actionsLabel}
              </th>
            </tr>
          </thead>
          <tbody class="divide-y divide-gray-100 bg-white">
            {#each displayItems as item, index}
              {@const itemId = getItemId(item)}
              <tr
                class="data-table-row hover:bg-yellow-50/70 transition-colors group cursor-pointer {selectable && selectedItems.has(itemId) ? 'bg-indigo-50/30' : ''}"
                onclick={(e) => handleRowClick(item, e)}
              >
                {#if selectable}
                  <td class="px-3 py-1.5" onclick={(e) => e.stopPropagation()}>
                    <input
                      type="checkbox"
                      checked={selectedItems.has(itemId)}
                      onchange={() => handleSelectItem(itemId)}
                      class="w-4 h-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500 cursor-pointer"
                      aria-label={$_("admin_content.bulk_actions.select_item", { values: { name: itemId } })}
                    />
                  </td>
                {/if}
                {#each effectiveIndexAttributes as attr}
                  <td class="px-4 py-1.5">
                    {@render cell({ item, attr, index })}
                  </td>
                {/each}
                <td class="px-4 py-1.5">
                  <div class="flex items-center justify-end gap-4">
                    {@render actions({ item, index })}
                  </div>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>

      {#if showPagination || items.length > 0}
        <div class="data-table-pagination">
          <div class="flex items-center justify-between gap-4">
            <div class="flex items-center gap-2">
              <span class="text-sm text-gray-500">
                {$_("admin_content.pagination.items_per_page") || "Items per page"}
              </span>
              <select
                value={itemsPerPage}
                onchange={handleItemsPerPageChange}
                class="bg-white border border-gray-200 text-sm font-medium text-gray-700 rounded-lg pl-3 pr-8 py-1.5 focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 cursor-pointer"
              >
                {#each itemsPerPageOptions as option}
                  <option value={option}>{option}</option>
                {/each}
              </select>
            </div>

            {#if showPagination}
              <div class="text-sm text-gray-500 hidden sm:block">
                {$_("admin_content.pagination.showing", {
                  values: {
                    start: formatNumber(paginationStart, $locale || "en"),
                    end: formatNumber(paginationEnd, $locale || "en"),
                    total: formatNumber(totalItems, $locale || "en"),
                  },
                })}
              </div>

              <div class="flex items-center gap-2 pagination-controls">
                <button
                  onclick={previousPage}
                  disabled={currentPage === 1}
                  class="pagination-btn"
                  aria-label={$_("admin_content.pagination.previous")}
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                    aria-hidden="true"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M15 19l-7-7 7-7"
                    />
                  </svg>
                </button>

                <span class="pagination-compact" aria-hidden="true">
                  {formatNumber(currentPage, $locale || "en")} / {formatNumber(totalPages, $locale || "en")}
                </span>

                <div class="flex items-center gap-1 pagination-pages">
                  {#if totalPages <= 7}
                    {#each Array(totalPages) as _, i}
                      <button
                        class="pagination-page-btn {currentPage === i + 1 ? 'pagination-page-btn-active' : ''}"
                        onclick={() => goToPage(i + 1)}
                      >
                        {formatNumber(i + 1, $locale || "en")}
                      </button>
                    {/each}
                  {:else}
                    <button
                      class="pagination-page-btn {currentPage === 1 ? 'pagination-page-btn-active' : ''}"
                      onclick={() => goToPage(1)}
                    >
                      {formatNumber(1, $locale || "en")}
                    </button>

                    {#if currentPage > 3}
                      <span class="pagination-ellipsis">...</span>
                    {/if}

                    {#each Array(totalPages) as _, i}
                      {#if i + 1 > 1 && i + 1 < totalPages && Math.abs(currentPage - (i + 1)) <= 1}
                        <button
                          class="pagination-page-btn {currentPage === i + 1 ? 'pagination-page-btn-active' : ''}"
                          onclick={() => goToPage(i + 1)}
                        >
                          {formatNumber(i + 1, $locale || "en")}
                        </button>
                      {/if}
                    {/each}

                    {#if currentPage < totalPages - 2}
                      <span class="pagination-ellipsis">...</span>
                    {/if}

                    <button
                      class="pagination-page-btn {currentPage === totalPages ? 'pagination-page-btn-active' : ''}"
                      onclick={() => goToPage(totalPages)}
                    >
                      {formatNumber(totalPages, $locale || "en")}
                    </button>
                  {/if}
                </div>

                <button
                  onclick={nextPage}
                  disabled={currentPage === totalPages}
                  class="pagination-btn"
                  aria-label={$_("admin_content.pagination.next")}
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M9 5l7 7-7 7"
                    />
                  </svg>
                </button>
              </div>
            {:else}
              <div class="text-sm text-gray-500">
                {$_("admin_content.pagination.total_items", {
                  values: { total: formatNumber(totalItems, $locale || "en") },
                })}
              </div>
            {/if}
          </div>
        </div>
      {/if}
    {/if}
  </div>
</div>

<style>
  .rtl {
    direction: rtl;
  }

  .data-table-container {
    width: 100%;
  }

  .data-table-card {
    background: var(--surface-card);
    border-radius: var(--radius-2xl);
    box-shadow: var(--shadow-sm);
    border: 1px solid var(--color-gray-100);
    overflow: hidden;
  }

  .skeleton-rows {
    padding: 1.5rem 1.75rem;
    display: flex;
    flex-direction: column;
    gap: 1.125rem;
  }

  .skeleton-row {
    display: flex;
    align-items: center;
    gap: 1.25rem;
    padding: 0.5rem 0;
  }

  .skeleton-row :global(.skeleton-block) {
    flex-shrink: 0;
  }

  .empty-state {
    text-align: center;
    padding: 4rem;
  }

  .empty-state-icon {
    width: 4rem;
    height: 4rem;
    background: var(--color-gray-50);
    border-radius: var(--radius-xl);
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1rem;
    color: var(--color-gray-400);
  }

  .empty-state-title {
    font-size: 1.125rem;
    font-weight: 700;
    color: var(--color-gray-900);
    margin-bottom: 0.5rem;
  }

  .empty-state-description {
    color: var(--color-gray-500);
    margin-bottom: 1.5rem;
  }

  .data-table-row {
    transition: background-color var(--duration-fast) ease;
  }

  .data-table-row:hover td:last-child > div {
    opacity: 1 !important;
  }

  .data-table-row:focus-visible {
    outline: 2px solid var(--color-primary-400);
    outline-offset: -2px;
  }

  .th-sort-btn {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    background: none;
    border: none;
    padding: 0;
    margin: 0;
    color: inherit;
    font: inherit;
    text-transform: inherit;
    letter-spacing: inherit;
    cursor: pointer;
    border-radius: var(--radius-sm);
  }

  .th-sort-btn:hover {
    color: var(--color-gray-800);
  }

  .th-sort-btn.is-active {
    color: var(--color-primary-600);
  }

  .th-sort-btn:focus-visible {
    outline: 2px solid var(--color-primary-400);
    outline-offset: 2px;
  }

  .th-sort-indicator {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 0.875rem;
    height: 0.875rem;
  }

  .data-table-pagination {
    padding: 1rem;
    border-top: 1px solid var(--color-gray-100);
    background: var(--color-gray-50);
  }

  .pagination-btn {
    padding: 0.5rem;
    background: var(--surface-card);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    color: var(--color-gray-500);
    transition: all var(--duration-normal) var(--ease-out);
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .pagination-btn:hover:not(:disabled) {
    background: var(--color-gray-50);
    color: var(--color-primary-600);
    border-color: var(--color-primary-100);
  }

  .pagination-btn:disabled {
    opacity: 0.4;
    cursor: not-allowed;
  }

  .pagination-page-btn {
    width: 2rem;
    height: 2rem;
    border-radius: var(--radius-md);
    font-size: 0.875rem;
    font-weight: 500;
    background: var(--surface-card);
    border: 1px solid var(--color-gray-200);
    color: var(--color-gray-500);
    transition: all var(--duration-normal) var(--ease-out);
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .pagination-page-btn:hover {
    background: var(--color-gray-50);
    color: var(--color-primary-600);
  }

  .pagination-page-btn-active {
    background: var(--color-primary-600) !important;
    color: white !important;
    border-color: var(--color-primary-600) !important;
    box-shadow: var(--shadow-brand);
  }

  .pagination-ellipsis {
    padding: 0 0.25rem;
    color: var(--color-gray-400);
  }

  .pagination-compact {
    display: none;
    padding: 0 0.375rem;
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-gray-600);
    white-space: nowrap;
  }

  @media (max-width: 640px) {
    .pagination-pages { display: none; }
    .pagination-compact { display: inline-flex; }
    .data-table-pagination .flex.items-center.justify-between {
      gap: 0.5rem;
    }
  }

  .bulk-actions-bar {
    background: var(--surface-card);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-lg);
    padding: 0.875rem 1.25rem;
    margin-bottom: 1rem;
    box-shadow: var(--shadow-md);
  }

  .bulk-actions-bar.rtl {
    direction: rtl;
  }

  .bulk-actions-content {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    flex-wrap: wrap;
  }

  .bulk-actions-info {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .bulk-actions-count {
    color: var(--color-gray-800);
    font-weight: 600;
    font-size: 0.9375rem;
  }

  .bulk-actions-buttons {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  @media (max-width: 640px) {
    .bulk-actions-content {
      flex-direction: column;
      align-items: stretch;
    }

    .bulk-actions-buttons {
      justify-content: stretch;
    }
  }
</style>
