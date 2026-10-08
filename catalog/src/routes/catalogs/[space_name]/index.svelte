<script lang="ts">
  import { isTotalUnknown, resolveTotal } from "@shared/query-total";
  import { onMount } from "svelte";
  import { goto as gotoStore, params } from "@roxi/routify";
  import { Dmart, QueryType, SortType, type DmartScope } from "@edraj/tsdmart";
  import {
    FileLinesOutline,
    FlagOutline,
    LayersOutline,
    PlusOutline,
    ShareNodesOutline,
    TagOutline,
  } from "flowbite-svelte-icons";
  import { getSpaceContentsByTags, getSpaces, getSpaceTags, searchInSpace } from "@/lib/dmart_services";
  import { getAvatarsCached } from "@/lib/dmart_services/avatars";
  import { _, locale } from "@/i18n";
  import { formatNumberInText } from "@/lib/helpers";
  import { absoluteUrl, catalogPath, withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";
  import { copyToClipboard } from "@/lib/toast";
  import {
    attachmentCounts,
    folderNameOf,
    isHot,
    itemDescription,
    itemTitle,
    localized,
    tagsOf,
    type AttachmentCounts,
    type CatalogRecord,
  } from "@/lib/catalogItems";
  import { getCurrentScope, user } from "@/stores/user";
  import Badge from "@/components/ui/Badge.svelte";
  import CatalogToolbar, { type SortOption, type SortOrder } from "@/components/ui/CatalogToolbar.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import type { MenuItem } from "@/components/ui/DropdownMenu.svelte";
  import PostCard from "@/components/post/PostCard.svelte";
  import PostCardSkeleton from "@/components/post/PostCardSkeleton.svelte";
  import ReportModal from "@/components/ReportModal.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  // A list row: the record plus what the card needs, computed once when the
  // page arrives — counts from the attachments the query already returned,
  // tags cleaned, the avatar filled in by one later state update.
  interface PostItem {
    record: CatalogRecord;
    key: string;
    owner: string;
    counts: AttachmentCounts;
    tags: string[];
    folder: string | null;
    avatarUrl: string | null;
  }

  let isLoading = $state(true);
  let isLoadingMore = $state(false);
  let error = $state<unknown>(null);
  let spaceName = $state("");
  let spaceRecord = $state<CatalogRecord | null>(null);

  let allContents = $state<PostItem[]>([]);
  let tagFilteredContents = $state<PostItem[]>([]);
  let searchResults = $state<PostItem[]>([]);

  // The toolbar binds `searchQuery` as the user types; `activeSearch` is the
  // query that was submitted and whose results are on screen.
  let searchQuery = $state("");
  let activeSearch = $state("");
  let isSearching = $state(false);
  // Search failures are shown next to the results; they must not replace the
  // whole page the way a listing failure does.
  let searchError = $state<unknown>(null);

  let sortBy = $state("created");
  let sortOrder = $state<SortOrder>("desc");
  let selectedContentTags = $state<string[]>([]);
  let availableContentTags = $state<string[]>([]);
  let tagCounts = $state<Record<string, number>>({});
  let showAllTags = $state(false);

  let showReportModal = $state(false);
  let reportItem = $state<PostItem | null>(null);
  // The reported item's own folder. Kept apart from the listing's subpath so
  // reporting an item from a sub-folder cannot redirect "Load more".
  let reportSubpath = $state("/");
  const subpath = "/";

  // Request sequence numbers: a response that arrives after a newer request
  // was issued is dropped instead of overwriting the newer results.
  let loadSeq = 0;
  let searchSeq = 0;

  let currentOffset = $state(0);
  let itemsPerLoad = $state(20);
  let totalItemsCount = $state(0);
  // False when the server skipped counting (total -1): the count is hidden
  // rather than shown as 0.
  let totalKnown = $state(true);
  let hasMoreItems = $state(true);

  let isTagFiltered = $state(false);
  let tagFilteredOffset = $state(0);
  let tagFilteredHasMore = $state(true);

  const itemsPerLoadOptions = [20, 50, 100];

  const sortOptions = $derived<SortOption[]>([
    { value: "created", label: $_("admin_dashboard.sort.created") },
    { value: "updated", label: $_("admin_dashboard.sort.updated") },
    { value: "name", label: $_("space.sort.name") },
    { value: "reactions", label: $_("space.sort.reactions") },
  ]);

  // Server-side sort field for each option. "reactions" has no server field
  // (the count comes from the attachments), so it is the one sort applied
  // client-side over the loaded rows — see sortLoadedRows.
  const SERVER_SORT_FIELD: Record<string, string | null> = {
    created: "created_at",
    updated: "updated_at",
    name: "shortname",
    reactions: null,
  };

  const selectClass =
    "h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary";

  // The hero shows the space's display name for the locale, not its shortname.
  const spaceTitle = $derived(localized(spaceRecord?.attributes?.displayname, $locale) || spaceName);
  const spaceDescription = $derived(
    spaceRecord ? itemDescription(spaceRecord, $locale) || $_("space.all_content_subtitle") : $_("space.all_content_subtitle"),
  );

  $effect(() => setTitle(spaceTitle));

  const menuItems = $derived<MenuItem[]>([
    { id: "share", label: $_("catalog_contents.card.share"), icon: ShareNodesOutline },
    { id: "report", label: $_("catalog_contents.card.report"), icon: FlagOutline },
  ]);

  const displayedTags = $derived(showAllTags ? availableContentTags : availableContentTags.slice(0, 12));

  // The one sort the server cannot do. Everything else arrives in server
  // order and is returned untouched so paging stays consistent.
  function sortLoadedRows(rows: PostItem[]): PostItem[] {
    if (sortBy !== "reactions") return rows;
    return [...rows].sort((a, b) => {
      const result = b.counts.reactions - a.counts.reactions;
      return sortOrder === "asc" ? -result : result;
    });
  }

  const displayedContents = $derived.by(() => {
    if (activeSearch) return sortLoadedRows(searchResults);
    return sortLoadedRows(isTagFiltered ? tagFilteredContents : allContents);
  });

  const canLoadMore = $derived(!activeSearch && (isTagFiltered ? tagFilteredHasMore : hasMoreItems));
  const hasActiveFilters = $derived(
    !!activeSearch || selectedContentTags.length > 0 || sortBy !== "created" || sortOrder !== "desc",
  );

  function serverSortBy(): string {
    return SERVER_SORT_FIELD[sortBy] ?? "created_at";
  }

  function serverSortType(): SortType {
    return sortOrder === "asc" ? SortType.ascending : SortType.descending;
  }

  function toItem(record: CatalogRecord): PostItem {
    return {
      record,
      key: `${record.subpath ?? "/"}/${record.shortname}`,
      owner: record.attributes?.owner_shortname ?? "",
      counts: attachmentCounts(record.attachments),
      tags: tagsOf(record),
      folder: folderNameOf(record.subpath),
      avatarUrl: null,
    };
  }

  // Avatars for every owner on the page, one cached lookup per owner, then ONE
  // assignment per list — never a request or a re-render per card.
  async function attachAvatars(items: PostItem[], seq: number, isSearch: boolean) {
    const urls = await getAvatarsCached(items.map((item) => item.owner));
    if (seq !== (isSearch ? searchSeq : loadSeq)) return;
    const patch = (list: PostItem[]) =>
      list.map((item) =>
        item.avatarUrl === null && urls.has(item.owner) ? { ...item, avatarUrl: urls.get(item.owner) ?? null } : item,
      );
    if (isSearch) {
      searchResults = patch(searchResults);
    } else if (isTagFiltered) {
      tagFilteredContents = patch(tagFilteredContents);
    } else {
      allContents = patch(allContents);
    }
  }

  async function loadSpaceRecord(scope: DmartScope) {
    try {
      const response = await getSpaces(false, scope);
      spaceRecord = (response.records as CatalogRecord[]).find((r) => r.shortname === spaceName) ?? null;
    } catch (err) {
      console.warn("Could not load the space record:", err);
    }
  }

  onMount(() => {
    spaceName = $params.space_name;
    void loadSpaceRecord(getCurrentScope());
    void loadContents(true);
  });

  async function loadContents(reset = false, tags: string[] = []) {
    const seq = ++loadSeq;
    // A reset keeps the current rows under an overlay until the new page
    // arrives; it never blanks the list to a spinner.
    if (reset) {
      isLoading = true;
      currentOffset = 0;
      if (tags.length > 0) {
        tagFilteredOffset = 0;
        isTagFiltered = true;
      } else {
        isTagFiltered = false;
      }
    } else {
      isLoadingMore = true;
    }
    error = null;

    try {
      const response =
        tags.length > 0
          ? await getSpaceContentsByTags(
              spaceName,
              "/",
              getCurrentScope(),
              itemsPerLoad,
              isTagFiltered ? tagFilteredOffset : currentOffset,
              tags,
              serverSortBy(),
              serverSortType(),
            )
          : await Dmart.query(
              {
                type: QueryType.search,
                space_name: spaceName,
                subpath,
                search: "-@shortname:schema -@resource_type:folder|schema",
                limit: itemsPerLoad,
                sort_by: serverSortBy(),
                sort_type: serverSortType(),
                offset: currentOffset,
                retrieve_json_payload: true,
                retrieve_attachments: true,
                exact_subpath: false,
              },
              getCurrentScope(),
            );
      if (seq !== loadSeq) return;

      totalKnown = !isTotalUnknown(response?.attributes?.total);
      totalItemsCount = resolveTotal(response?.attributes?.total);

      if (!response || !response.records) {
        if (reset) {
          if (isTagFiltered) {
            tagFilteredContents = [];
            tagFilteredHasMore = false;
          } else {
            allContents = [];
            availableContentTags = [];
            hasMoreItems = false;
          }
        }
        return;
      }

      const items = (response.records as CatalogRecord[]).map(toItem);

      if (isTagFiltered) {
        tagFilteredContents = reset ? items : [...tagFilteredContents, ...items];
        tagFilteredHasMore = items.length === itemsPerLoad;
        tagFilteredOffset += itemsPerLoad;
      } else {
        allContents = reset ? items : [...allContents, ...items];
        hasMoreItems = items.length === itemsPerLoad;
        currentOffset += itemsPerLoad;
        if (reset) void loadContentTags();
      }

      void attachAvatars(items, seq, false);
    } catch (err) {
      if (seq !== loadSeq) return;
      console.error("Error fetching space contents:", err);
      error = err;
      if (reset) {
        if (isTagFiltered) {
          tagFilteredContents = [];
          tagFilteredHasMore = false;
        } else {
          allContents = [];
          availableContentTags = [];
          hasMoreItems = false;
        }
      }
    } finally {
      if (seq === loadSeq) {
        isLoading = false;
        isLoadingMore = false;
      }
    }
  }

  async function performSearch(query: string) {
    const seq = ++searchSeq;
    searchError = null;
    activeSearch = query.trim();
    if (!activeSearch) {
      searchResults = [];
      isSearching = false;
      return;
    }

    isSearching = true;
    try {
      // Scoped to this space: result links build /catalogs/{this space}/...
      // so a hit from another space would open the wrong entry.
      const results = await searchInSpace(
        spaceName,
        activeSearch,
        itemsPerLoad,
        serverSortBy(),
        serverSortType(),
        getCurrentScope(),
      );
      if (seq !== searchSeq) return;
      const items = (results as CatalogRecord[]).map(toItem);
      searchResults = items;
      void attachAvatars(items, seq, true);
    } catch (err) {
      if (seq !== searchSeq) return;
      console.error("Error performing search:", err);
      searchError = err;
      searchResults = [];
    } finally {
      if (seq === searchSeq) isSearching = false;
    }
  }

  function clearSearch() {
    searchSeq++;
    searchQuery = "";
    activeSearch = "";
    searchResults = [];
    searchError = null;
    isSearching = false;
  }

  // Any change of sort order re-queries: the server orders the whole set,
  // not just the rows already on screen.
  function handleSortChange() {
    if (activeSearch) void performSearch(activeSearch);
    else void loadContents(true, selectedContentTags);
  }

  // The space's tag cloud: one query per (re)load, not one per page.
  async function loadContentTags() {
    try {
      const response = await getSpaceTags(spaceName);
      const attributes = response?.records?.[0]?.attributes as
        | { tags?: unknown; tag_counts?: Record<string, unknown> }
        | undefined;
      availableContentTags = Array.isArray(attributes?.tags)
        ? attributes.tags.filter((t): t is string => typeof t === "string" && t.length > 0)
        : [];
      const counts: Record<string, number> = {};
      for (const [tag, count] of Object.entries(attributes?.tag_counts ?? {})) counts[tag] = Number(count) || 0;
      tagCounts = counts;
    } catch (err) {
      console.warn("Could not load the space tags:", err);
      availableContentTags = [];
      tagCounts = {};
    }
  }

  function loadMoreItems() {
    if (isLoadingMore || !canLoadMore) return;
    void loadContents(false, isTagFiltered ? selectedContentTags : []);
  }

  function handleItemsPerLoadChange(event: Event) {
    itemsPerLoad = parseInt((event.currentTarget as HTMLSelectElement).value, 10) || 20;
    if (activeSearch) void performSearch(activeSearch);
    else void loadContents(true, selectedContentTags);
  }

  function itemPath(item: PostItem): string {
    return catalogPath({
      space: spaceName,
      subpath: item.record.subpath || "/",
      shortname: item.record.shortname,
      resourceType: item.record.resource_type ?? "content",
    });
  }

  function handleNewPost() {
    // The create page reads space_name from the query string to preselect
    // the space (see routes/entries/create.svelte loadPrefilledData).
    goto("/entries/create", { space_name: spaceName });
  }

  function toggleContentTag(tag: string) {
    selectedContentTags = selectedContentTags.includes(tag)
      ? selectedContentTags.filter((t) => t !== tag)
      : [...selectedContentTags, tag];
    if (selectedContentTags.length > 0) {
      void loadContents(true, selectedContentTags);
    } else {
      isTagFiltered = false;
      void loadContents(true, []);
    }
  }

  function clearAllFilters() {
    selectedContentTags = [];
    clearSearch();
    sortBy = "created";
    sortOrder = "desc";
    isTagFiltered = false;
    tagFilteredContents = [];
    tagFilteredOffset = 0;
    tagFilteredHasMore = true;
    void loadContents(true, []);
  }

  async function shareItem(item: PostItem) {
    const url = absoluteUrl(itemPath(item));
    const title = itemTitle(item.record, $locale);
    if (typeof navigator !== "undefined" && navigator.share) {
      try {
        await navigator.share({
          title,
          text: $_("catalog_contents.card.share_text", { values: { title } }),
          url,
        });
        return;
      } catch (err) {
        // The user dismissed the share sheet; nothing to report.
        if ((err as { name?: string })?.name === "AbortError") return;
      }
    }
    await copyToClipboard(url, { copied: $_("ui.link_copied"), failed: $_("ui.copy_failed") });
  }

  function openReportModal(item: PostItem) {
    reportItem = item;
    reportSubpath = item.record.subpath || "/";
    showReportModal = true;
  }

  function onCardMenu(item: PostItem, action: string) {
    if (action === "share") void shareItem(item);
    else if (action === "report") openReportModal(item);
  }

  const number = (n: number) => formatNumberInText(n, $locale ?? "");
</script>

<div class="mx-auto w-full max-w-5xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={spaceTitle}
    description={spaceDescription}
    icon={LayersOutline}
    backHref="/catalogs"
    backLabel={$_("post_detail.breadcrumb.catalogs")}
  >
    {#snippet actions()}
      {#if $user.signedin}
        <button type="button" class="app-btn app-btn-primary" onclick={handleNewPost}>
          <PlusOutline size="sm" aria-hidden="true" />
          {$_("space.new_post")}
        </button>
      {/if}
    {/snippet}
  </PageHeader>

  <!-- Real numbers only: the server's total when it counted, otherwise the
       rows on screen; the tag count from the tag query. -->
  <div class="flex flex-wrap items-center gap-2 mb-6">
    <Badge>
      <FileLinesOutline size="xs" aria-hidden="true" />
      {number(totalKnown && totalItemsCount > 0 ? totalItemsCount : displayedContents.length)}
      {$_("space.stats.posts")}
    </Badge>
    {#if availableContentTags.length > 0}
      <Badge>
        <TagOutline size="xs" aria-hidden="true" />
        {number(availableContentTags.length)}
        {$_("space.stats.tags")}
      </Badge>
    {/if}
  </div>

  {#if availableContentTags.length > 0 && !activeSearch}
    <section class="mb-5" aria-label={$_("space.filter_by_tag_label")}>
      <p class="text-xs font-medium text-text-muted uppercase tracking-wide mb-2">{$_("space.filter_by_tag_label")}</p>
      <div class="flex flex-wrap gap-2">
        {#each displayedTags as tag (tag)}
          {@const selected = selectedContentTags.includes(tag)}
          <button
            type="button"
            class="tag-pill"
            class:selected
            aria-pressed={selected}
            onclick={() => toggleContentTag(tag)}
          >
            <span>#{tag}</span>
            {#if tagCounts[tag]}
              <span class="tag-count tabular-nums">{number(tagCounts[tag])}</span>
            {/if}
          </button>
        {/each}
        {#if availableContentTags.length > 12}
          <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={() => (showAllTags = !showAllTags)}>
            {showAllTags ? $_("space.show_less_tags") : $_("space.show_all_tags")}
          </button>
        {/if}
      </div>
    </section>
  {/if}

  <CatalogToolbar
    bind:search={searchQuery}
    placeholder={$_("space.search_posts_placeholder")}
    onSearch={(q) => void performSearch(q)}
    onClear={clearSearch}
    bind:sort={sortBy}
    {sortOptions}
    onSortChange={handleSortChange}
    bind:order={sortOrder}
    onOrderChange={handleSortChange}
    class="mb-4"
  >
    {#snippet filters()}
      <label for="items-per-load-select" class="sr-only">{$_("catalog_contents.infinite_scroll.items_per_load")}</label>
      <select
        id="items-per-load-select"
        value={itemsPerLoad}
        onchange={handleItemsPerLoadChange}
        class={selectClass}
        title={$_("catalog_contents.infinite_scroll.items_per_load")}
      >
        {#each itemsPerLoadOptions as option (option)}
          <option value={option}>{option}</option>
        {/each}
      </select>
    {/snippet}
    {#if hasActiveFilters}
      <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={clearAllFilters}>
        {$_("catalog_contents.filters.clear_all")}
      </button>
    {/if}
  </CatalogToolbar>

  {#if isLoading && displayedContents.length === 0}
    <div class="post-list" role="status" aria-busy="true" aria-label={$_("ui.loading")}>
      {#each Array.from({ length: 4 }, (_, i) => i) as i (i)}
        <PostCardSkeleton preview={false} />
      {/each}
    </div>
  {:else if error}
    <ErrorState title={$_("catalog_contents.error.title")} {error} onRetry={() => void loadContents(true, selectedContentTags)} />
  {:else}
    <p class="text-sm text-text-muted mb-4" aria-live="polite">
      {#if activeSearch}
        {#if isSearching}
          {$_("ui.loading")}
        {:else if !searchError}
          {$_("space.search_results_count", {
            values: { count: number(searchResults.length), query: activeSearch },
          })}
        {/if}
      {:else if displayedContents.length > 0}
        {#if totalKnown && totalItemsCount > 0}
          {$_("space.showing_posts", {
            values: { displayed: number(displayedContents.length), total: number(totalItemsCount) },
          })}
        {:else}
          {$_("catalog_contents.infinite_scroll.showing_count", {
            values: { displayed: number(displayedContents.length) },
          })}
        {/if}
      {/if}
    </p>

    {#if activeSearch && isSearching}
      <div class="post-list" aria-busy="true">
        {#each Array.from({ length: 3 }, (_, i) => i) as i (i)}
          <PostCardSkeleton preview={false} />
        {/each}
      </div>
    {:else if activeSearch && searchError}
      <ErrorState error={searchError} onRetry={() => void performSearch(activeSearch)} />
    {:else if displayedContents.length === 0}
      <EmptyState
        title={$_("catalog_contents.empty.title")}
        hint={hasActiveFilters ? $_("catalog_contents.empty.no_matches") : $_("catalog_contents.empty.space_empty")}
      >
        {#if hasActiveFilters}
          <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearAllFilters}>
            {$_("catalog_contents.filters.clear_all")}
          </button>
        {/if}
      </EmptyState>
    {:else}
      <LoadingState variant="overlay" loading={isLoading || isLoadingMore} label={$_("catalog_contents.pagination.loading")}>
        <div class="post-list">
          {#each displayedContents as item (item.key)}
            <PostCard
              href={withBase(itemPath(item))}
              title={itemTitle(item.record, $locale)}
              author={item.owner || $_("space.unknown_user")}
              avatarUrl={item.avatarUrl}
              date={item.record.attributes?.created_at}
              folder={item.folder ?? $_("space.general")}
              tags={item.tags}
              selectedTags={selectedContentTags}
              onTagClick={activeSearch ? undefined : toggleContentTag}
              counts={item.counts}
              hot={isHot(item.counts)}
              {menuItems}
              onMenuSelect={(action) => onCardMenu(item, action)}
            />
          {/each}
        </div>
      </LoadingState>

      {#if canLoadMore}
        <div class="flex justify-center mt-6">
          <button type="button" class="app-btn app-btn-secondary" onclick={loadMoreItems} disabled={isLoadingMore || isLoading}>
            {$_("catalog_contents.pagination.load_more")} ({number(itemsPerLoad)})
          </button>
        </div>
      {:else if !activeSearch}
        <p class="end-of-results">{$_("space.end_of_results")}</p>
      {/if}
    {/if}
  {/if}
</div>

<ReportModal
  bind:isVisible={showReportModal}
  entryShortname={reportItem?.record.shortname || ""}
  entryTitle={reportItem ? itemTitle(reportItem.record, $locale) : ""}
  {spaceName}
  subpath={reportSubpath}
  onClose={() => {
    showReportModal = false;
    reportItem = null;
  }}
  onReportSubmitted={() => {
    showReportModal = false;
    reportItem = null;
  }}
/>

<style>
  .post-list {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .tag-pill {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.25rem 0.75rem;
    border-radius: var(--radius-full);
    border: 1px solid var(--color-border);
    background: var(--color-surface-2);
    color: var(--color-text-muted);
    font-size: var(--font-size-sm);
    font-weight: var(--font-weight-medium);
    cursor: pointer;
    transition: background var(--duration-fast) var(--ease-out), color var(--duration-fast) var(--ease-out),
      border-color var(--duration-fast) var(--ease-out);
  }

  .tag-pill:hover {
    background: var(--color-surface-3);
    color: var(--color-text);
  }

  .tag-pill.selected {
    background: var(--color-primary-soft);
    border-color: var(--color-primary);
    color: var(--color-primary);
  }

  .tag-count {
    color: var(--color-text-faint);
    font-size: var(--font-size-xs);
  }

  .tag-pill.selected .tag-count {
    color: var(--color-primary);
  }

  .end-of-results {
    margin: 2.5rem 0 1rem;
    text-align: center;
    font-size: var(--font-size-xs);
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--color-text-faint);
  }
</style>
