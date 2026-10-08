<script lang="ts">
  import { resolveTotal } from "@shared/query-total";
  import { onMount } from "svelte";
  import { QueryType, type DmartScope } from "@edraj/tsdmart";
  import {
    FileLinesOutline,
    GlobeOutline,
    LayersOutline,
    SearchOutline,
    UserOutline,
    UsersOutline,
  } from "flowbite-svelte-icons";
  import { getSpaceContents, getSpaces, getSpaceTags, searchInCatalog } from "@/lib/dmart_services";
  import { getAllUsers } from "@/lib/dmart_services/users";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { formatNumber, formatNumberInText } from "@/lib/helpers";
  import { catalogPath, withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";
  import { itemDescription, itemTitle, previewText, tagsOf, type CatalogRecord } from "@/lib/catalogItems";
  import { getCurrentScope } from "@/stores/user";
  import { website } from "@/config";
  import Badge from "@/components/ui/Badge.svelte";
  import Card from "@/components/ui/Card.svelte";
  import CatalogToolbar, { type SortOption, type SortOrder } from "@/components/ui/CatalogToolbar.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import SkeletonBlock from "@/components/SkeletonBlock.svelte";
  import PostCard from "@/components/post/PostCard.svelte";
  import PostCardSkeleton from "@/components/post/PostCardSkeleton.svelte";

  interface SpaceTag {
    name: string;
    count: number;
  }

  interface SpaceRecord extends CatalogRecord {
    attributes?: CatalogRecord["attributes"] & { hide_space?: boolean; ordinal?: number };
  }

  interface SearchHit {
    record: CatalogRecord & { space_name?: string };
    preview: string;
  }

  $effect(() => setTitle($_("catalogs.page_title")));

  let isLoading = $state(true);
  let isStatsLoading = $state(true);
  let spaces = $state<SpaceRecord[]>([]);
  let error = $state<unknown>(null);

  // The toolbar binds `searchQuery` as the user types; `activeSearch` is the
  // query that was actually submitted and whose results are on screen.
  let searchQuery = $state("");
  let activeSearch = $state("");
  let sortBy = $state("name");
  let sortOrder = $state<SortOrder>("asc");
  let filterTags = $state("all");
  let filterActive = $state("all");
  let searchResults = $state<SearchHit[]>([]);
  let isSearching = $state(false);
  // A failed search is reported beside the results, not as a page error:
  // the spaces are still loaded and usable.
  let searchError = $state<unknown>(null);
  // Drops a search response that arrives after a newer query was submitted.
  let searchSeq = 0;

  let spaceTotals = $state<Record<string, number>>({});
  let totalUsers = $state(0);
  let spaceTags = $state<Record<string, SpaceTag[]>>({});

  const sortOptions = $derived<SortOption[]>([
    { value: "name", label: $_("catalogs.filter.name") },
    { value: "created", label: $_("catalogs.filter.newest") },
    { value: "entries", label: $_("catalogs.filter.most_entries") },
  ]);

  const selectClass =
    "h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary";

  // Every tag seen across the loaded spaces, most used first — the real
  // values behind the Tags filter.
  const allTags = $derived.by(() => {
    const counts = new Map<string, number>();
    for (const tags of Object.values(spaceTags)) {
      for (const tag of tags) counts.set(tag.name, (counts.get(tag.name) ?? 0) + tag.count);
    }
    return [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([name]) => name);
  });

  const totalSpaceItems = $derived(Object.values(spaceTotals).reduce((sum, n) => sum + n, 0));

  // Filtering and sorting are derived from the loaded data, so a locale
  // switch (display names) or the stats arriving re-sorts without any effect.
  const filteredSpaces = $derived.by(() => {
    let filtered = [...spaces];
    if (filterActive !== "all") {
      filtered = filtered.filter((space) =>
        filterActive === "active" ? space.attributes?.is_active : !space.attributes?.is_active,
      );
    }
    if (filterTags !== "all") {
      filtered = filtered.filter((space) => tagsForSpace(space.shortname).some((tag) => tag.name === filterTags));
    }
    filtered.sort((a, b) => {
      let result: number;
      switch (sortBy) {
        case "created":
          result =
            new Date(b.attributes?.created_at || 0).getTime() - new Date(a.attributes?.created_at || 0).getTime();
          break;
        case "entries":
          // The one popularity signal the index actually has: how much
          // content each space holds (loaded with the stats).
          result = totalFor(b.shortname) - totalFor(a.shortname);
          break;
        default:
          result = spaceName(a).localeCompare(spaceName(b), $locale ?? undefined);
      }
      return sortOrder === "desc" ? -result : result;
    });
    return filtered;
  });

  const hasActiveFilters = $derived(filterTags !== "all" || filterActive !== "all");

  async function loadSpaceSummary(shortname: string, scope: DmartScope): Promise<{ total: number; tags: SpaceTag[] }> {
    // The two calls per space run in parallel; all spaces run in parallel
    // (Promise.allSettled in loadStats), so the landing page waits one round
    // trip for its numbers instead of 2 × N.
    const [counters, tags] = await Promise.all([
      getSpaceContents(shortname, "/", scope, 100, 0, false, QueryType.counters),
      getSpaceTags(shortname),
    ]);
    const tagCounts = tags?.records?.[0]?.attributes?.tag_counts as Record<string, unknown> | undefined;
    const sortedTags: SpaceTag[] = tagCounts
      ? Object.entries(tagCounts)
          .map(([name, count]) => ({ name, count: Number(count) || 0 }))
          .sort((a, b) => b.count - a.count)
      : [];
    return { total: resolveTotal(counters?.attributes?.total), tags: sortedTags };
  }

  async function loadSpaces() {
    const scope = getCurrentScope();
    isLoading = true;
    error = null;
    try {
      const response = await getSpaces(false, scope);
      spaces = (response.records || []) as SpaceRecord[];
    } catch (err) {
      console.error("Error fetching spaces:", err);
      error = err;
      spaces = [];
    } finally {
      isLoading = false;
    }
    if (!error) await loadStats(scope);
  }

  // Stats, users and tags in the background, settled per space so one space
  // the visitor cannot read does not blank every other space's numbers.
  async function loadStats(scope: DmartScope) {
    isStatsLoading = true;
    const [usersResult, ...summaryResults] = await Promise.allSettled([
      getAllUsers(0, 0),
      ...spaces.map((space) => loadSpaceSummary(space.shortname, scope)),
    ]);

    if (usersResult.status === "fulfilled") {
      totalUsers = resolveTotal(usersResult.value?.attributes?.total);
    } else {
      console.error("Error fetching users count:", usersResult.reason);
    }

    const totals: Record<string, number> = {};
    const tags: Record<string, SpaceTag[]> = {};
    summaryResults.forEach((result, index) => {
      const shortname = spaces[index].shortname;
      if (result.status === "fulfilled") {
        totals[shortname] = result.value.total;
        tags[shortname] = result.value.tags;
      } else {
        console.error(`Error fetching stats for space "${shortname}":`, result.reason);
        tags[shortname] = [];
      }
    });
    spaceTotals = totals;
    spaceTags = tags;
    isStatsLoading = false;
  }

  onMount(() => {
    void loadSpaces();
  });

  function tagsForSpace(shortname: string): SpaceTag[] {
    return spaceTags[shortname] || [];
  }

  function totalFor(shortname: string): number {
    return spaceTotals[shortname] ?? 0;
  }

  function hasTotal(shortname: string): boolean {
    return shortname in spaceTotals;
  }

  function spaceHref(space: SpaceRecord): string {
    const path =
      website.use_admin_space_view === true
        ? `/dashboard/admin/${encodeURIComponent(space.shortname)}`
        : catalogPath({ space: space.shortname });
    return withBase(path);
  }

  function hitHref(hit: SearchHit): string {
    // searchInCatalog tags each hit with the space it came from.
    const record = hit.record;
    const space = record.space_name ?? (record.attributes?.space_name as string | undefined) ?? "";
    return withBase(
      catalogPath({
        space,
        subpath: record.subpath || "/",
        shortname: record.shortname,
        resourceType: record.resource_type ?? "content",
      }),
    );
  }

  function spaceName(space: SpaceRecord): string {
    return itemTitle(space, $locale) || $_("catalogs.unnamed_space");
  }

  function spaceDescription(space: SpaceRecord): string {
    return itemDescription(space, $locale) || $_("catalogs.no_description");
  }

  function initial(space: SpaceRecord): string {
    return (spaceName(space) || space.shortname || "S").charAt(0).toUpperCase();
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
      const results = await searchInCatalog(activeSearch, 20);
      if (seq !== searchSeq) return;
      // The excerpt is built once per result, not on every render.
      searchResults = (results as SearchHit["record"][]).map((record) => ({
        record,
        preview: itemDescription(record, $locale) || previewText(record.attributes?.payload, 200),
      }));
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

  function clearFilters() {
    filterTags = "all";
    filterActive = "all";
  }

  const number = (n: number) => formatNumber(n, $locale ?? "");
</script>

<div class="mx-auto w-full max-w-7xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={$_("catalogs.page_title")}
    description={$_("catalogs.hero.subtitle")}
    icon={LayersOutline}
  />

  <!-- Real numbers only: spaces loaded, members and entries counted. -->
  <div class="grid grid-cols-1 sm:grid-cols-3 gap-3 mb-6">
    {#snippet stat(Icon: typeof LayersOutline, value: string, label: string, pending: boolean)}
      <Card padding="sm" class="stat-card">
        <div class="flex items-center gap-3">
          <div
            class="shrink-0 w-10 h-10 rounded-full flex items-center justify-center bg-primary-soft text-primary"
            aria-hidden="true"
          >
            <Icon size="md" />
          </div>
          <div class="min-w-0" aria-busy={pending}>
            {#if pending}
              <SkeletonBlock width="3rem" height="1.25rem" radius="var(--radius-full)" />
            {:else}
              <p class="text-xl font-semibold text-text leading-tight tabular-nums">{value}</p>
            {/if}
            <p class="text-xs text-text-muted">{label}</p>
          </div>
        </div>
      </Card>
    {/snippet}
    {@render stat(LayersOutline, number(spaces.length), $_("catalogs.stats.active_spaces"), isLoading)}
    {@render stat(UsersOutline, number(totalUsers), $_("catalogs.stats.members"), isStatsLoading)}
    {@render stat(FileLinesOutline, number(totalSpaceItems), $_("catalogs.stats.posts"), isStatsLoading)}
  </div>

  <CatalogToolbar
    bind:search={searchQuery}
    placeholder={$_("catalogs.search.placeholder")}
    onSearch={(q) => void performSearch(q)}
    onClear={clearSearch}
    bind:sort={sortBy}
    {sortOptions}
    bind:order={sortOrder}
    class="mb-6"
  >
    {#snippet filters()}
      <label for="catalog-tags-filter" class="sr-only">{$_("catalogs.filter.tags")}</label>
      <select
        id="catalog-tags-filter"
        bind:value={filterTags}
        class={selectClass}
        disabled={isStatsLoading || allTags.length === 0}
      >
        <option value="all">{$_("catalogs.filter.all_tags")}</option>
        {#each allTags as tag (tag)}
          <option value={tag}>{tag}</option>
        {/each}
      </select>
      <label for="catalog-status-filter" class="sr-only">{$_("catalog_contents.filters.status")}</label>
      <select id="catalog-status-filter" bind:value={filterActive} class={selectClass}>
        <option value="all">{$_("catalog_contents.filters.all_statuses")}</option>
        <option value="active">{$_("catalog_contents.filters.active")}</option>
        <option value="inactive">{$_("catalog_contents.filters.inactive")}</option>
      </select>
    {/snippet}
    {#if hasActiveFilters}
      <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={clearFilters}>
        {$_("catalog_contents.filters.clear_all")}
      </button>
    {/if}
  </CatalogToolbar>

  {#if isLoading}
    <div class="spaces-grid" role="status" aria-busy="true" aria-label={$_("ui.loading")}>
      {#each Array.from({ length: 6 }, (_, i) => i) as i (i)}
        <Card padding="none" class="space-card" aria-hidden="true">
          <div class="space-strip space-strip-skeleton"></div>
          <div class="space-body gap-2">
            <SkeletonBlock width="60%" height="1rem" radius="var(--radius-full)" />
            <SkeletonBlock width="35%" height="0.625rem" radius="var(--radius-full)" />
            <SkeletonBlock width="100%" height="0.75rem" radius="var(--radius-full)" class="mt-2" />
            <SkeletonBlock width="80%" height="0.75rem" radius="var(--radius-full)" />
          </div>
        </Card>
      {/each}
    </div>
  {:else if error}
    <ErrorState title={$_("catalogs.error.title")} {error} onRetry={() => void loadSpaces()} />
  {:else if activeSearch}
    <section aria-live="polite">
      <div class="flex flex-wrap items-center justify-between gap-3 mb-4">
        <div>
          <h2 class="text-lg font-semibold text-text">{$_("catalogs.search.results_title")}</h2>
          <p class="text-sm text-text-muted">
            {#if isSearching}
              {$_("ui.loading")}
            {:else}
              {$_("catalogs.search.results_count", {
                values: { count: formatNumberInText(searchResults.length, $locale ?? ""), query: activeSearch },
              })}
            {/if}
          </p>
        </div>
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearSearch}>
          {$_("catalogs.search.clear")}
        </button>
      </div>

      {#if isSearching}
        <div class="results-grid" aria-busy="true">
          {#each Array.from({ length: 3 }, (_, i) => i) as i (i)}
            <PostCardSkeleton />
          {/each}
        </div>
      {:else if searchError}
        <ErrorState error={searchError} onRetry={() => void performSearch(activeSearch)} />
      {:else if searchResults.length === 0}
        <EmptyState
          icon={SearchOutline}
          title={$_("catalogs.empty.no_results_title")}
          hint={$_("catalogs.empty.no_results_description")}
        >
          <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearSearch}>
            {$_("catalogs.search.clear")}
          </button>
        </EmptyState>
      {:else}
        <div class="results-grid">
          {#each searchResults as hit (`${hit.record.space_name}/${hit.record.subpath}/${hit.record.shortname}`)}
            <PostCard
              href={hitHref(hit)}
              title={itemTitle(hit.record, $locale)}
              author={hit.record.attributes?.owner_shortname || $_("common.unknown")}
              date={hit.record.attributes?.created_at}
              folder={hit.record.space_name ?? null}
              preview={hit.preview}
              tags={tagsOf(hit.record)}
            />
          {/each}
        </div>
      {/if}
    </section>
  {:else if filteredSpaces.length === 0}
    <EmptyState
      icon={GlobeOutline}
      title={$_("catalogs.empty.no_catalogs_title")}
      hint={hasActiveFilters ? $_("catalog_contents.empty.no_matches") : $_("catalogs.empty.no_catalogs_description")}
    >
      {#if hasActiveFilters}
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearFilters}>
          {$_("catalog_contents.filters.clear_all")}
        </button>
      {/if}
    </EmptyState>
  {:else}
    <p class="text-sm text-text-muted mb-4">
      {$_("catalogs.showing_spaces", {
        values: { count: formatNumberInText(filteredSpaces.length, $locale ?? "") },
      })}
    </p>

    <div class="spaces-grid">
      {#each filteredSpaces as space (space.shortname)}
        {@const tags = tagsForSpace(space.shortname)}
        <Card href={spaceHref(space)} padding="none" class="space-card">
          <div class="space-strip" aria-hidden="true">{initial(space)}</div>
          <div class="space-body">
            <h3 class="text-base font-semibold text-text leading-snug break-words">{spaceName(space)}</h3>
            <p
              class="mt-0.5 inline-flex items-center gap-1 text-xs text-text-muted min-w-0"
              title={$_("catalogs.card.owner_aria", { values: { name: space.attributes?.owner_shortname || $_("common.unknown") } })}
            >
              <UserOutline size="xs" aria-hidden="true" />
              <span class="truncate">{space.attributes?.owner_shortname || $_("common.unknown")}</span>
            </p>
            <p class="mt-2 text-sm text-text-muted leading-relaxed line-clamp-2 break-words">{spaceDescription(space)}</p>

            {#if isStatsLoading}
              <div class="mt-3 flex gap-1.5" aria-hidden="true">
                <SkeletonBlock width="3rem" height="1.125rem" radius="var(--radius-full)" />
                <SkeletonBlock width="4rem" height="1.125rem" radius="var(--radius-full)" />
              </div>
            {:else if tags.length > 0}
              <div class="mt-3 flex flex-wrap gap-1.5">
                {#each tags.slice(0, 3) as tag (tag.name)}
                  <Badge variant="primary" size="sm">{tag.name}</Badge>
                {/each}
                {#if tags.length > 3}
                  <Badge size="sm">
                    {$_("catalog_contents.tags.more", { values: { count: formatNumberInText(tags.length - 3, $locale ?? "") } })}
                  </Badge>
                {/if}
              </div>
            {/if}

            <div class="space-meta">
              {#if isStatsLoading}
                <SkeletonBlock width="3rem" height="0.75rem" radius="var(--radius-full)" />
              {:else if hasTotal(space.shortname)}
                <span
                  class="inline-flex items-center gap-1 tabular-nums"
                  title={$_("catalogs.card.entries_aria", { values: { count: number(totalFor(space.shortname)) } })}
                >
                  <FileLinesOutline size="xs" aria-hidden="true" />
                  <span aria-hidden="true">{number(totalFor(space.shortname))}</span>
                  <span class="sr-only">
                    {$_("catalogs.card.entries_aria", { values: { count: number(totalFor(space.shortname)) } })}
                  </span>
                </span>
              {:else}
                <span></span>
              {/if}
              {#if space.attributes?.created_at}
                <time datetime={space.attributes.created_at} class="ms-auto">
                  {formatDate(space.attributes.created_at, "relative", $locale)}
                </time>
              {/if}
            </div>
          </div>
        </Card>
      {/each}
    </div>
  {/if}
</div>

<style>
  .spaces-grid {
    display: grid;
    grid-template-columns: 1fr;
    gap: 1rem;
    align-items: stretch;
  }

  @media (min-width: 640px) {
    .spaces-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  @media (min-width: 1280px) {
    .spaces-grid {
      grid-template-columns: repeat(3, minmax(0, 1fr));
    }
  }

  @media (min-width: 1700px) {
    .spaces-grid {
      grid-template-columns: repeat(4, minmax(0, 1fr));
    }
  }

  .results-grid {
    display: grid;
    grid-template-columns: 1fr;
    gap: 1rem;
  }

  @media (min-width: 1024px) {
    .results-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  /* The Card is the <a>; stretch it and its one wrapper so every card in a
     row is the same height and the meta row sits on the bottom edge. */
  .spaces-grid :global(.space-card) {
    height: 100%;
    overflow: hidden;
  }

  .spaces-grid :global(.space-card > div) {
    display: flex;
    flex-direction: column;
    height: 100%;
  }

  .space-strip {
    flex-shrink: 0;
    height: 4.25rem;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    font-size: 1.75rem;
    font-weight: var(--font-weight-bold);
    letter-spacing: 0.02em;
  }

  .space-strip-skeleton {
    background: var(--color-surface-3);
  }

  .space-body {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-width: 0;
    padding: 1rem 1.25rem 1.125rem;
  }

  .space-meta {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-top: auto;
    padding-top: 0.875rem;
    font-size: var(--font-size-xs);
    color: var(--color-text-muted);
  }
</style>
