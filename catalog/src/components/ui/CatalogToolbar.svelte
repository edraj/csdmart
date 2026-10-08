<script module lang="ts">
  export type SortOption = { value: string; label: string };
  export type SortOrder = "asc" | "desc";
</script>

<script lang="ts">
  import type { Snippet } from "svelte";
  import { ArrowDownOutline, ArrowUpOutline, CloseOutline, SearchOutline } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import IconButton from "./IconButton.svelte";

  // The browse-page toolbar: search (clear + submit) at the start, sort
  // select + order toggle, a slot for page-specific filters, actions at the
  // end. Wraps on phones. `search`, `sort` and `order` are bindable; the page
  // hears about search only on submit/clear (never per keystroke) and about
  // sort/order on change, so it can refetch with sort_by/sort_type.
  let {
    search = $bindable(""),
    placeholder,
    onSearch,
    onClear,
    sort = $bindable(""),
    sortOptions = [],
    onSortChange,
    order = $bindable<SortOrder>("desc"),
    onOrderChange,
    filters,
    children,
    class: className = "",
  }: {
    search?: string;
    placeholder?: string;
    /** Called on submit (Enter or the search button). Omit to hide the search box. */
    onSearch?: (query: string) => void;
    onClear?: () => void;
    sort?: string;
    /** Omit or leave empty to hide the sort control. */
    sortOptions?: SortOption[];
    onSortChange?: (sort: string) => void;
    order?: SortOrder;
    onOrderChange?: (order: SortOrder) => void;
    /** Page-specific filter controls (tag chips, type select …). */
    filters?: Snippet;
    /** Actions, rendered at the end. */
    children?: Snippet;
    class?: string;
  } = $props();

  const uid = $props.id();

  function submit(event: Event) {
    event.preventDefault();
    onSearch?.(search.trim());
  }

  function clear() {
    search = "";
    onClear?.();
    onSearch?.("");
  }

  function changeSort(event: Event) {
    sort = (event.currentTarget as HTMLSelectElement).value;
    onSortChange?.(sort);
  }

  function toggleOrder() {
    order = order === "asc" ? "desc" : "asc";
    onOrderChange?.(order);
  }

  const searchLabel = $derived(placeholder ?? $_("ui.search"));
  const orderLabel = $derived(order === "asc" ? $_("ui.sort_ascending") : $_("ui.sort_descending"));
</script>

<div class="flex flex-wrap items-center gap-3 {className}" role="toolbar" aria-label={$_("ui.toolbar")}>
  {#if onSearch}
    <form onsubmit={submit} class="relative flex items-center w-full sm:w-72" role="search">
      <SearchOutline
        size="sm"
        class="absolute start-3 text-text-faint pointer-events-none"
        aria-hidden="true"
      />
      <input
        type="search"
        bind:value={search}
        placeholder={searchLabel}
        aria-label={searchLabel}
        class="w-full h-9 ps-9 pe-16 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary"
      />
      <span class="absolute end-1 flex items-center">
        {#if search}
          <IconButton label={$_("ui.clear_search")} size="sm" onclick={clear}>
            <CloseOutline size="sm" />
          </IconButton>
        {/if}
        <IconButton label={$_("ui.search")} size="sm" type="submit">
          <SearchOutline size="sm" />
        </IconButton>
      </span>
    </form>
  {/if}

  {#if sortOptions.length > 0}
    <div class="flex items-center gap-1">
      <label for="{uid}-sort" class="sr-only">{$_("ui.sort_by")}</label>
      <select
        id="{uid}-sort"
        value={sort}
        onchange={changeSort}
        class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
      >
        {#each sortOptions as option (option.value)}
          <option value={option.value}>{option.label}</option>
        {/each}
      </select>
      <IconButton
        label="{$_('ui.toggle_sort_order')}: {orderLabel}"
        variant="outline"
        onclick={toggleOrder}
      >
        {#if order === "asc"}
          <ArrowUpOutline size="sm" />
        {:else}
          <ArrowDownOutline size="sm" />
        {/if}
      </IconButton>
    </div>
  {/if}

  {#if filters}
    <div class="flex flex-wrap items-center gap-2">
      {@render filters()}
    </div>
  {/if}

  {#if children}
    <div class="flex flex-wrap items-center gap-2 ms-auto">
      {@render children()}
    </div>
  {/if}
</div>
